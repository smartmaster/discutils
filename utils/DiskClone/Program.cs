﻿//
// Copyright (c) 2008-2010, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DiscUtils;
using DiscUtils.Common;
using DiscUtils.Partitions;
using DiscUtils.Ntfs;
using System.Security.Principal;

namespace DiskClone
{
    class CloneVolume
    {
        public NativeMethods.DiskExtent SourceExtent;
        public string Path;
        public Guid SnapshotId;
        public VssSnapshotProperties SnapshotProperties;
    }

    class Program : ProgramBase
    {
        private CommandLineMultiParameter _volumes;
        private CommandLineParameter _destDisk;

        static void Main(string[] args)
        {
            Program program = new Program();
            program.Run(args);
        }

        protected override StandardSwitches DefineCommandLine(CommandLineParser parser)
        {
            _volumes = new CommandLineMultiParameter("volume", "Volumes to clone.  The volumes should all be on the same disk.", false);
            _destDisk = new CommandLineParameter("out_file", "Path to the output disk image.", false);

            parser.AddMultiParameter(_volumes);
            parser.AddParameter(_destDisk);

            return StandardSwitches.OutputFormat;
        }

        protected override string[] HelpRemarks
        {
            get
            {
                return new string[]
                {
                    "DiskClone clones a live disk into a virtual disk file.  The volumes cloned must be formatted with NTFS, and partitioned using a conventional partition table.",
                    "Only Windows 7 is supported.",
                    "The tool must be run with administrator privilege."
                };
            }
        }

        protected override void DoRun()
        {
            if (!IsAdministrator())
            {
                Console.WriteLine("\nThis utility must be run as an administrator!\n");
                Environment.Exit(1);
            }


            string[] sourceVolume = _volumes.Values;

            uint diskNumber;

            List<CloneVolume> cloneVolumes = GatherVolumes(sourceVolume, out diskNumber);


            Geometry geom;
            long capacity;
            byte[] mbr;
            GetDiskDetails(diskNumber, out geom, out capacity, out mbr);

            IVssBackupComponents backupCmpnts;
            int status = NativeMethods.CreateVssBackupComponents(out backupCmpnts);


            Guid snapshotSetId = CreateSnapshotSet(cloneVolumes, backupCmpnts);

            if (!Quiet)
            {
                Console.Write("Copying Disk...");
            }


            // Construct a stream representing the contents of the cloned disk.
            BiosPartitionedDiskBuilder contentBuilder = new BiosPartitionedDiskBuilder(capacity, mbr, geom);
            foreach (var sv in cloneVolumes)
            {
                Volume sourceVol = new Volume(sv.SnapshotProperties.SnapshotDeviceObject, sv.SourceExtent.ExtentLength);

                SnapshotStream rawVolStream = new SnapshotStream(sourceVol.Content, Ownership.None);
                rawVolStream.Snapshot();

                byte[] volBitmap;
                int clusterSize;
                using (NtfsFileSystem ntfs = new NtfsFileSystem(rawVolStream))
                {
                    ntfs.NtfsOptions.HideSystemFiles = false;
                    ntfs.NtfsOptions.HideHiddenFiles = false;
                    ntfs.NtfsOptions.HideMetafiles = false;

                    // Remove VSS snapshot files (can be very large)
                    foreach (string filePath in ntfs.GetFiles(@"\System Volume Information", "*{3808876B-C176-4e48-B7AE-04046E6CC752}"))
                    {
                        ntfs.DeleteFile(filePath);
                    }

                    // Remove the page file
                    if (ntfs.FileExists(@"\Pagefile.sys"))
                    {
                        ntfs.DeleteFile(@"\Pagefile.sys");
                    }

                    // Remove the hibernation file
                    if (ntfs.FileExists(@"\hiberfil.sys"))
                    {
                        ntfs.DeleteFile(@"\hiberfil.sys");
                    }

                    using (Stream bitmapStream = ntfs.OpenFile(@"$Bitmap", FileMode.Open))
                    {
                        volBitmap = new byte[bitmapStream.Length];

                        int totalRead = 0;
                        int numRead = bitmapStream.Read(volBitmap, 0, volBitmap.Length - totalRead);
                        while (numRead > 0)
                        {
                            totalRead += numRead;
                            numRead = bitmapStream.Read(volBitmap, totalRead, volBitmap.Length - totalRead);
                        }
                    }

                    clusterSize = (int)ntfs.ClusterSize;
                }

                List<StreamExtent> extents = new List<StreamExtent>(BitmapToRanges(volBitmap, clusterSize));
                SparseStream partSourceStream = SparseStream.FromStream(sourceVol.Content, Ownership.None, extents);

                for (int i = 0; i < contentBuilder.PartitionTable.Partitions.Count; ++i )
                {
                    var part = contentBuilder.PartitionTable.Partitions[i];
                    if (part.FirstSector * 512 == sv.SourceExtent.StartingOffset)
                    {
                        contentBuilder.SetPartitionContent(i, partSourceStream);
                    }
                }
            }
            SparseStream contentStream = contentBuilder.Build();


            // Write out the disk images
            string dir = Path.GetDirectoryName(_destDisk.Value);
            string file = Path.GetFileNameWithoutExtension(_destDisk.Value);
            string ext = Path.GetExtension(_destDisk.Value);

            DiskImageBuilder builder = DiskImageBuilder.GetBuilder(OutputDiskType, OutputDiskVariant);
            builder.Content = contentStream;
            DiskImageFileSpecification[] fileSpecs = builder.Build(file);

            for (int i = 0; i < fileSpecs.Length; ++i)
            {
                // Construct the destination file path from the directory of the primary file.
                string outputPath = Path.Combine(dir, fileSpecs[i].Name);

                // Force the primary file to the be one from the command-line.
                if (i == 0)
                {
                    outputPath = _destDisk.Value;
                }

                using (SparseStream vhdStream = fileSpecs[i].OpenStream())
                using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite))
                {
                    StreamPump pump = new StreamPump()
                    {
                        InputStream = vhdStream,
                        OutputStream = fs,
                    };

                    long totalBytes = 0;
                    foreach (var se in vhdStream.Extents)
                    {
                        totalBytes += se.Length;
                    }

                    if (!Quiet)
                    {
                        Console.WriteLine();
                        DateTime now = DateTime.Now;
                        pump.ProgressEvent += (o, e) => { ShowProgress(fileSpecs[i].Name, totalBytes, now, o, e); };
                    }

                    pump.Run();
                }
            }


            // Complete - tidy up
            CallAsyncMethod(backupCmpnts.BackupComplete);

            long numDeleteFailed;
            Guid deleteFailed;
            backupCmpnts.DeleteSnapshots(snapshotSetId, 2 /*VSS_OBJECT_SNAPSHOT_SET*/, true, out numDeleteFailed, out deleteFailed);

            Marshal.ReleaseComObject(backupCmpnts);
        }

        private static bool IsAdministrator()
        {
            WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static IEnumerable<StreamExtent> BitmapToRanges(byte[] bitmap, int bytesPerCluster)
        {
            long numClusters = bitmap.Length * 8;
            long cluster = 0;
            while (cluster < numClusters && !IsSet(bitmap, cluster))
            {
                ++cluster;
            }

            while (cluster < numClusters)
            {
                long startCluster = cluster;
                while (cluster < numClusters && IsSet(bitmap, cluster))
                {
                    ++cluster;
                }

                yield return new StreamExtent((long)(startCluster * (long)bytesPerCluster), (long)((cluster - startCluster) * (long)bytesPerCluster));

                while (cluster < numClusters && !IsSet(bitmap, cluster))
                {
                    ++cluster;
                }
            }
        }

        private static bool IsSet(byte[] buffer, long bit)
        {
            int byteIdx = (int)(bit >> 3);
            if (byteIdx >= buffer.Length)
            {
                return false;
            }

            byte val = buffer[byteIdx];
            byte mask = (byte)(1 << (int)(bit & 0x7));

            return (val & mask) != 0;
        }

        private static void ShowProgress(string label, long totalBytes, DateTime startTime, object sourceObject, PumpProgressEventArgs e)
        {
            int progressLen = 55 - label.Length;

            int numProgressChars = (int)((e.BytesRead * progressLen) / totalBytes);
            string progressBar = new string('=', numProgressChars) + new string(' ', progressLen - numProgressChars);

            DateTime now = DateTime.Now;
            TimeSpan timeSoFar = now - startTime;

            TimeSpan remaining = TimeSpan.FromMilliseconds((timeSoFar.TotalMilliseconds / (double)e.BytesRead) * (totalBytes - e.BytesRead));

            Console.Write("\r{0} ({1,3}%)  |{2}| {3}", label, (e.BytesRead * 100) / totalBytes, progressBar, remaining.ToString(@"hh\:mm\:ss\.f"));
        }

        private List<CloneVolume> GatherVolumes(string[] sourceVolume, out uint diskNumber)
        {
            diskNumber = uint.MaxValue;

            List<CloneVolume> cloneVolumes = new List<CloneVolume>(sourceVolume.Length);

            if (!Quiet)
            {
                Console.WriteLine("Inspecting Volumes...");
            }

            for (int i = 0; i < sourceVolume.Length; ++i)
            {
                using (Volume vol = new Volume(sourceVolume[i], 0))
                {
                    NativeMethods.DiskExtent[] sourceExtents = vol.GetDiskExtents();
                    if (sourceExtents.Length > 1)
                    {
                        Console.Error.WriteLine("Volume '{0}' is made up of multiple extents, which is not supported", sourceVolume[i]);
                        Environment.Exit(1);
                    }

                    if (diskNumber == uint.MaxValue)
                    {
                        diskNumber = sourceExtents[0].DiskNumber;
                    }
                    else if (diskNumber != sourceExtents[0].DiskNumber)
                    {
                        Console.Error.WriteLine("Specified volumes span multiple disks, which is not supported");
                        Environment.Exit(1);
                    }

                    string volPath = sourceVolume[i];
                    if (volPath[volPath.Length - 1] != '\\')
                    {
                        volPath += @"\";
                    }

                    cloneVolumes.Add(new CloneVolume { Path = volPath, SourceExtent = sourceExtents[0] });
                }
            }

            return cloneVolumes;
        }

        private void GetDiskDetails(uint diskNumber, out Geometry geom, out long capacity, out byte[] mbr)
        {
            if (!Quiet)
            {
                Console.WriteLine("Inspecting Disk...");
            }

            using (Disk disk = new Disk(diskNumber))
            {
                geom = disk.GetGeometry();
                capacity = disk.GetLength();

                mbr = new byte[512];
                disk.Content.Position = 0;
                if (disk.Content.Read(mbr, 0, mbr.Length) != mbr.Length)
                {
                    Console.Error.WriteLine("Failed to read MBR from {0}", @"\\.\PhysicalDrive" + diskNumber);
                    Environment.Exit(1);
                }
            }
        }

        private Guid CreateSnapshotSet(List<CloneVolume> cloneVolumes, IVssBackupComponents backupCmpnts)
        {
            if (!Quiet)
            {
                Console.WriteLine("Snapshotting Volumes...");
            }

            backupCmpnts.InitializeForBackup(null);
            backupCmpnts.SetContext(0 /* VSS_CTX_BACKUP */);

            backupCmpnts.SetBackupState(false, true, 5 /* VSS_BT_COPY */, false);

            CallAsyncMethod(backupCmpnts.GatherWriterMetadata);

            Guid snapshotSetId;
            try
            {
                backupCmpnts.StartSnapshotSet(out snapshotSetId);
                foreach (var vol in cloneVolumes)
                {
                    backupCmpnts.AddToSnapshotSet(vol.Path, Guid.Empty, out vol.SnapshotId);
                }

                CallAsyncMethod(backupCmpnts.PrepareForBackup);

                CallAsyncMethod(backupCmpnts.DoSnapshotSet);
            }
            catch
            {
                backupCmpnts.AbortBackup();
                throw;
            }

            foreach (var vol in cloneVolumes)
            {
                vol.SnapshotProperties = GetSnapshotProperties(backupCmpnts, vol.SnapshotId);
            }

            return snapshotSetId;
        }

        private static VssSnapshotProperties GetSnapshotProperties(IVssBackupComponents backupComponents, Guid snapshotId)
        {
            VssSnapshotProperties props = new VssSnapshotProperties();

            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(VssSnapshotProperties)));

            backupComponents.GetSnapshotProperties(snapshotId, buffer);

            Marshal.PtrToStructure(buffer, props);

            NativeMethods.VssFreeSnapshotProperties(buffer);
            return props;
        }

        private delegate void VssAsyncMethod(out IVssAsync result);

        private static void CallAsyncMethod(VssAsyncMethod method)
        {
            IVssAsync async;
            int reserved = 0;
            uint hResult;

            method(out async);

            async.Wait(60 * 1000);

            async.QueryStatus(out hResult, ref reserved);

            if (hResult != 0 && hResult != 0x0004230a /* VSS_S_ASYNC_FINISHED */)
            {
                Marshal.ThrowExceptionForHR((int)hResult);
            }

        }
    }
}