//
// Copyright (c) 2008-2011, Kenneth Bell
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

namespace MSBuildTask
{
    using System;
    using System.Collections.Generic;
    using DiscUtils.Iso9660;
    using Microsoft.Build.Framework;
    using Microsoft.Build.Utilities;

    public class CreateIso : Task
    {
        public CreateIso()
        {
            UseJoliet = true;
        }

        /// <summary>
        /// The name of the ISO file to create.
        /// </summary>
        [Required]
        public ITaskItem FileName { get; set; }

        /// <summary>
        /// Whether to use Joliet encoding for the ISO (default true).
        /// </summary>
        public bool UseJoliet { get; set; }

        /// <summary>
        /// The label for the ISO (may be truncated if too long)
        /// </summary>
        public string VolumeLabel { get; set; }

        /// <summary>
        /// The files to add to the ISO.
        /// </summary>
        [Required]
        public ITaskItem[] SourceFiles { get; set; }

        /// <summary>
        /// Sets the root to remove from the source files.
        /// </summary>
        /// <remarks>
        /// If the source file is C:\MyDir\MySubDir\file.txt, and RemoveRoot is C:\MyDir, the ISO will
        /// contain \MySubDir\file.txt.  If not specified, the file would be named \MyDir\MySubDir\file.txt.
        /// </remarks>
        public ITaskItem RemoveRoot { get; set; }

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.Normal, "Creating ISO file: '{0}'", FileName.ItemSpec);

            try
            {
                CDBuilder builder = new CDBuilder();
                builder.UseJoliet = UseJoliet;

                if (!string.IsNullOrEmpty(VolumeLabel))
                {
                    builder.VolumeIdentifier = VolumeLabel;
                }

                foreach (var sourceFile in SourceFiles)
                {
                    if (this.RemoveRoot != null)
                    {
                        string location = (sourceFile.GetMetadata("FullPath")).Replace(this.RemoveRoot.GetMetadata("FullPath"), string.Empty);
                        builder.AddFile(location, sourceFile.GetMetadata("FullPath"));
                    }
                    else
                    {
                        builder.AddFile(sourceFile.GetMetadata("Directory") + sourceFile.GetMetadata("FileName") + sourceFile.GetMetadata("Extension"), sourceFile.GetMetadata("FullPath"));
                    }
                }

                builder.Build(FileName.ItemSpec);
            }
            catch(Exception e)
            {
                Log.LogErrorFromException(e, true, true, null);
                return false;
            }

            return !Log.HasLoggedErrors;
        }
    }
}
