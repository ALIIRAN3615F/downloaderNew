using HtmlAgilityPack;
using R34Downloader.Models;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace R34Downloader.Services
{
    public static class R34HtmlService
    {
        private const string ContentUrl = "https://rule34.xxx/index.php?page=post&s=list&tags=";
        private const byte PageSize = 42;

        public static bool IsSomethingFound(string tags)
        {
            var document = LoadHtmlDocument($"{ContentUrl}{tags}");
            var nodes = document.DocumentNode.SelectNodes("//div[@class='content']//span[@class='thumb']");
            return nodes != null;
        }

        public static int GetMaxPid(string tags)
        {
            var document = LoadHtmlDocument($"{ContentUrl}{tags}");
            var nodes = document.DocumentNode.SelectSingleNode("//div[@class='pagination']//a[@alt='last page']");
            var pidString = nodes?.GetAttributeValue("href", null);

            if (pidString == null)
                return default;

            var maxPid = pidString.Substring(pidString.LastIndexOf('=') + 1);
            return Convert.ToInt32(maxPid);
        }

        public static int GetCountContent(string tags, int pid)
        {
            var document = LoadHtmlDocument($"{ContentUrl}{tags}&pid={pid}");
            var nodes = document.DocumentNode.SelectNodes("//div[@class='content']//span[@class='thumb']/a");

            if (nodes != null && pid == 0)
                return nodes.Count;

            if (nodes != null)
                return pid + nodes.Count;

            return ushort.MaxValue;
        }

        public static void DownloadContent(string path, string tags, ushort quantity, IProgress<int> progress, IProgress<int> progress2)
        {
            var maxPages = quantity;
            ushort residue = PageSize;

            if (quantity < PageSize)
            {
                maxPages = PageSize;
                residue = quantity;
            }

            for (var pid = 0; pid < maxPages; pid += PageSize)
            {
                var document = LoadHtmlDocument($"{ContentUrl}{tags}&pid={pid}");
                var nodes = document.DocumentNode.SelectNodes("//div[@class='content']//span[@class='thumb']/a");

                var posts = nodes.Select(x => x.GetAttributeValue("href", "").Replace("&amp;", "&"))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToArray();

                DownloadPosts(posts, path, pid, residue, maxPages, progress, progress2);
            }
        }

        private static void DownloadPosts(string[] posts, string path, int pid, int residue, int maxPages, IProgress<int> progress, IProgress<int> progress2)
        {
            var maxPosts = posts.Length;

            if (maxPages - pid < PageSize)
                maxPosts = maxPages - pid;
            else if (maxPages - pid == PageSize)
                maxPosts = residue;

            for (var i = 0; i < maxPosts; i++)
            {
                var document = LoadHtmlDocument($"https://rule34.xxx/{posts[i]}");

                var videoNode = document.DocumentNode.SelectSingleNode("//video[@id='gelcomVideoPlayer']/source");
                var imageNode = document.DocumentNode.SelectSingleNode("//meta[@property='og:image']");

                if (videoNode != null && SettingsModel.Video)
                {
                    var videoUrl = videoNode.GetAttributeValue("src", null);
                    if (videoUrl != null)
                    {
                        var filename = Path.GetFileName(videoUrl);
                        var questionMarkIndex = filename.IndexOf('?');
                        if (questionMarkIndex > 0)
                        {
                            filename = Path.GetFileName(filename[..questionMarkIndex]);
                        }

                        DownloadService.Download(videoUrl, Path.Combine(path, "Video", filename));
                    }
                }
                else
                {
                    var imageUrl = imageNode?.GetAttributeValue("content", null);
                    if (imageUrl != null)
                    {
                        var id = imageUrl.Split('?')[1];
                        imageUrl = imageUrl[..imageUrl.LastIndexOf('?')];
                        var filename = $"{id}{Path.GetExtension(imageUrl)}";

                        if (filename.Contains(".gif") && SettingsModel.Gif)
                            DownloadService.Download(imageUrl, Path.Combine(path, "Gif", filename));
                        else if (!filename.Contains(".gif") && SettingsModel.Images)
                            DownloadService.Download(imageUrl, Path.Combine(path, "Images", filename));
                    }
                }

                var reportStatus = pid + i + 1;
                progress.Report(reportStatus);
                progress2.Report(reportStatus);

                Thread.Sleep(100);
            }
        }

        private static HtmlDocument LoadHtmlDocument(string url)
        {
            var htmlWeb = new HtmlWeb
            {
                PreRequest = request =>
                {
                    if (request != null)
                    {
                        request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                        request.Referer = "https://rule34.xxx/";
                        request.CookieContainer = new CookieContainer();

                        request.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
                        request.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));
                    }

                    return true;
                }
            };

            return htmlWeb.Load(url);
        }
    }
}
