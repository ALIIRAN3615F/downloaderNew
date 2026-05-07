using System;
using R34Downloader.Services;

namespace R34Downloader
{
    class Program
    {
        static void Main(string[] args)
        {
            // دریافت پارامترها از command line
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: R34Downloader <tags> <quantity> <output_path>");
                return;
            }

            var tags = args[0];
            var quantity = ushort.Parse(args[1]);
            var outputPath = args[2];

            Console.WriteLine($"Starting download: tags={tags}, quantity={quantity}");

            // Progress reporters
            var progress1 = new Progress<int>(value => Console.WriteLine($"Progress: {value}"));
            var progress2 = new Progress<int>(value => { }); // dummy

            try
            {
                // استفاده از API service (سریع‌تر)
                var count = R34ApiService.GetContentCount(tags);
                Console.WriteLine($"Found {count} items");

                if (count > 0)
                {
                    R34ApiService.DownloadContent(outputPath, tags, quantity, progress1, progress2);
                    Console.WriteLine("Download completed!");
                }
                else
                {
                    Console.WriteLine("No content found");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }
    }
}
