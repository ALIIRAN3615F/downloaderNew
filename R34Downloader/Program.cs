using R34Downloader.Services;
using System;
using System.IO;

namespace R34Downloader
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: R34Downloader <tags> <quantity>");
                Console.WriteLine("Example: R34Downloader \"anime solo\" 50");
                return;
            }

            var tags = args[0];
            if (!ushort.TryParse(args[1], out var quantity))
            {
                Console.WriteLine("Error: Quantity must be a valid number");
                return;
            }

            // مسیر خروجی به صورت خودکار تعیین می‌شود
            var downloadPath = Path.Combine(Directory.GetCurrentDirectory(), "Downloads");
            Directory.CreateDirectory(downloadPath);

            Console.WriteLine($"Starting download: Tags='{tags}', Quantity={quantity}");
            Console.WriteLine($"Output path: {downloadPath}");

            var progress = new Progress<int>(value =>
            {
                Console.WriteLine($"Progress: {value}/{quantity}");
            });

            var progress2 = new Progress<int>(value =>
            {
                if (value % 10 == 0)
                {
                    Console.WriteLine($"Downloaded {value} files...");
                }
            });

            try
            {
                var contentCount = R34ApiService.GetContentCount(tags);
                Console.WriteLine($"Found {contentCount} items for tags: {tags}");

                if (contentCount == 0)
                {
                    Console.WriteLine("No content found. Trying HTML service...");
                    if (R34HtmlService.IsSomethingFound(tags))
                    {
                        R34HtmlService.DownloadContent(downloadPath, tags, quantity, progress, progress2);
                    }
                    else
                    {
                        Console.WriteLine("No content found with HTML service either.");
                        return;
                    }
                }
                else
                {
                    R34ApiService.DownloadContent(downloadPath, tags, quantity, progress, progress2);
                }

                Console.WriteLine($"Download completed! Files saved to: {downloadPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }
    }
}
