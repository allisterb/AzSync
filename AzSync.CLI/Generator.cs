using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;

using System.Threading;
using System.Threading.Tasks;

namespace AzSync.CLI
{
    internal class Generator : ILogging
    {
        public static async Task GenerateFile(CancellationToken ct, string fileName, int sizeMB, int averagePartSizeKB = 100)
        {
            double size = sizeMB * 1024 * 1024;
            long averagePartSize = averagePartSizeKB * 1024;
            string fullPath = Path.GetFullPath(fileName);
            int fileCount = (int) Math.Round(size / averagePartSize);
            L.Info("Writing {parts} part(s) with average size {s} KB to file {file}.", fileCount, averagePartSizeKB, fullPath);
            using (Package package = ZipPackage.Open(fullPath, FileMode.Create))
            {
                for (int i = 0; i < fileCount;  i++)
                {
                    byte[] buffer = new byte[averagePartSize];
                    PackagePart part = package.CreatePart(new Uri("/" + Guid.NewGuid(), UriKind.Relative), "text/plain");
                    using (Stream partStream = part.GetStream(FileMode.Create))
                    {
                        r.NextBytes(buffer);
                        await partStream.WriteAsync(buffer, 0, buffer.Length, ct);
                    }
                }
                package.Flush();
                L.Info("Successfully generated test file {0}.", fullPath);
            }
        }

        public static async Task ModifyFile(CancellationToken ct, FileInfo file, int sizeMB, int averagePartSizeKB = 100)
        {
            double size = sizeMB * 1024 * 1024;
            long averagePartSize = averagePartSizeKB * 1024;
            double difference = size - file.Length;
            using (Package package = ZipPackage.Open(file.FullName, FileMode.Open))
            {
                if (difference > 0)
                {
                    int filesToAdd = (int) Math.Round(difference / averagePartSize);
                    L.Info("Adding {parts} part(s) with average size {average} KB to file {file}", filesToAdd, averagePartSizeKB, file.FullName);
                    for (int i = 0; i < filesToAdd; i++)
                    {
                        byte[] buffer = new byte[averagePartSize];
                        PackagePart part = package.CreatePart(new Uri("/" + Guid.NewGuid(), UriKind.Relative), "text/plain");
                        using (Stream partStream = part.GetStream(FileMode.Create))
                        {
                            r.NextBytes(buffer);
                            await partStream.WriteAsync(buffer, 0, buffer.Length, ct);
                        }
                    }
                }
                else
                {
                    int filesToRemove = (int)Math.Round(Math.Abs(difference) / averagePartSize);
                    L.Info("Removing {parts} parts(s) from file {file}.", filesToRemove, file.FullName);
                    PackagePart[] parts = package.GetParts().OrderBy(o => r.Next(0, 10)).Take(filesToRemove).ToArray();
                    foreach (PackagePart part in parts)
                    {
                        package.DeletePart(part.Uri);
                    }
                }
                package.Flush();
            }
            //FileInfo modifiedfile = new FileInfo(file.FullName);
            file.Refresh();
            L.Info("Successfully modified test file {0} to size {1} bytes.", file.FullName, file.Length);
        }

        #region Fields
        static Random r = new Random();
        static Logger<Generator> L = new Logger<Generator>();
        #endregion

    }
}
