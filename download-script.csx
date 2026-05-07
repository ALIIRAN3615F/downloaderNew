#!/usr/bin/env dotnet-script
#r "nuget: HtmlAgilityPack, 1.11.46"

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Xml;
using System.Linq;
using System.Threading;
using HtmlAgilityPack;

// ----------- SettingsModel -----------
public static class SettingsModel
{
    public static ushort Limit { get; set; }
    public static bool Images { get; set; }
    public static bool Gif { get; set; }
    public static bool Video { get; set; }
    public static bool IsApi { get; set; }
}

// ----------- DownloadService -----------
public static class DownloadService
{
    public static void Download(string url, string filePath)
    {
        if (File.Exists(filePath)) return;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        handler.CookieContainer.Add(new Cookie
        {
            Name = "gdpr", Value = "1",
            Domain = "rule34.xxx", Path = "/",
            Expires = DateTime.Now.AddYears(1)
        });
        handler.CookieContainer.Add(new Cookie
        {
            Name = "gdpr-consent", Value = "1",
            Domain = "rule34.xxx", Path = "/",
            Expires = DateTime.Now.AddYears(1)
        });

        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");

        try
        {
            var response = client.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            var data = response.Content.ReadAsByteArrayAsync().Result;
            File.WriteAllBytes(filePath, data);
        }
        catch { /* skip */ }
    }
}

// ----------- R34ApiService -----------
public static class R34ApiService
{
    private const string ApiUrl = "https://rule34.xxx/index.php?page=dapi&s=post&q=index";
    private const byte PageSize = 100;

    public static void DownloadContent(string path, string tags, ushort quantity,
        IProgress<int> progress, IProgress<int> progress2)
    {
        var maxPid = quantity <= PageSize ? 1 : quantity % PageSize == 0 ? quantity / PageSize - 1 : quantity / PageSize;

        for (var pid = 0; pid <= maxPid; pid++)
        {
            var doc = new XmlDocument();
            doc.Load($"{ApiUrl}&tags={tags}&pid={pid}");

            var postCount = quantity - pid * PageSize < PageSize ? quantity - pid * PageSize : PageSize;
            for (var i = 0; i < postCount; i++)
            {
                var url = doc.DocumentElement?.ChildNodes[i].Attributes?.GetNamedItem("file_url")?.Value;
                var fileExtension = Path.GetExtension(url);
                var filename = doc.DocumentElement?.ChildNodes[i].Attributes?.GetNamedItem("id")?.Value + fileExtension;

                if (url != null)
                {
                    if ((fileExtension == ".mp4" || fileExtension == ".webm") && SettingsModel.Video)
                    {
                        var sampleUrl = doc.DocumentElement?.ChildNodes[i].Attributes?.GetNamedItem("sample_url")?.Value ?? url;
                        DownloadService.Download(sampleUrl, Path.Combine(path, "Video", filename));
                    }
                    else if (fileExtension == ".gif" && SettingsModel.Gif)
                    {
                        DownloadService.Download(url, Path.Combine(path, "Gif", filename));
                    }
                    else if (fileExtension != ".mp4" && fileExtension != ".webm" && fileExtension != ".gif" && SettingsModel.Images)
                    {
                        DownloadService.Download(url, Path.Combine(path, "Images", filename));
                    }
                }

                progress.Report(pid * 100 + i + 1);
                progress2.Report(pid * 100 + i + 1);
                Thread.Sleep(100);
            }
        }
    }
}

// ----------- R34HtmlService -----------
public static class R34HtmlService
{
    private const string ContentUrl = "https://rule34.xxx/index.php?page=post&s=list&tags=";
    private const byte PageSize = 42;

    public static void DownloadContent(string path, string tags, ushort quantity,
        IProgress<int> progress, IProgress<int> progress2)
    {
        var maxPages = quantity;
        ushort residue = PageSize;

        if (quantity < PageSize) { maxPages = PageSize; residue = quantity; }

        for (var pid = 0; pid < maxPages; pid += PageSize)
        {
            var doc = LoadHtmlDoc($"{ContentUrl}{tags}&pid={pid}");
            var nodes = doc.DocumentNode.SelectNodes("//div[@class='content']//span[@class='thumb']/a");
            var posts = nodes.Select(x => x.GetAttributeValue("href", "").Replace("&amp;", "&"))
                .Where(x => !string.IsNullOrEmpty(x)).ToArray();

            DownloadPosts(posts, path, pid, residue, maxPages, progress, progress2);
        }
    }

    private static void DownloadPosts(string[] posts, string path, int pid, int residue, int maxPages,
        IProgress<int> progress, IProgress<int> progress2)
    {
        var maxPosts = posts.Length;
        if (maxPages - pid < PageSize) maxPosts = maxPages - pid;
        else if (maxPages - pid == PageSize) maxPosts = residue;

        for (var i = 0; i < maxPosts; i++)
        {
            var doc = LoadHtmlDoc($"https://rule34.xxx/{posts[i]}");
            var videoNode = doc.DocumentNode.SelectSingleNode("//video[@id='gelcomVideoPlayer']/source");
            var imageNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");

            if (videoNode != null && SettingsModel.Video)
            {
                var videoUrl = videoNode.GetAttributeValue("src", null);
                if (videoUrl != null)
                {
                    var filename = Path.GetFileName(videoUrl);
                    var qmIdx = filename.IndexOf('?');
                    if (qmIdx > 0) filename = Path.GetFileName(filename.Substring(0, qmIdx));
                    DownloadService.Download(videoUrl, Path.Combine(path, "Video", filename));
                }
            }
            else
            {
                var imgUrl = imageNode?.GetAttributeValue("content", null);
                if (imgUrl != null)
                {
                    var id = imgUrl.Split('?')[1];
                    imgUrl = imgUrl.Substring(0, imgUrl.LastIndexOf('?'));
                    var filename = $"{id}{Path.GetExtension(imgUrl)}";

                    if (filename.Contains(".gif") && SettingsModel.Gif)
                        DownloadService.Download(imgUrl, Path.Combine(path, "Gif", filename));
                    else if (!filename.Contains(".gif") && SettingsModel.Images)
                        DownloadService.Download(imgUrl, Path.Combine(path, "Images", filename));
                }
            }

            progress.Report(pid + i + 1);
            progress2.Report(pid + i + 1);
            Thread.Sleep(100);
        }
    }

    private static HtmlDocument LoadHtmlDoc(string url)
    {
        var web = new HtmlWeb
        {
            PreRequest = request =>
            {
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
                request.Referer = "https://rule34.xxx/";
                request.Host = "rule34.xxx";
                request.CookieContainer = new CookieContainer();
                request.CookieContainer.Add(new Cookie { Name = "gdpr", Value = "1", Domain = "rule34.xxx", Path = "/", Expires = DateTime.Now.AddYears(1) });
                request.CookieContainer.Add(new Cookie { Name = "gdpr-consent", Value = "1", Domain = "rule34.xxx", Path = "/", Expires = DateTime.Now.AddYears(1) });
                return true;
            }
        };
        return web.Load(url);
    }
}

// ============= MAIN =============
var args = Args.ToArray();  // dotnet-script special array
string tags = null;
ushort quantity = 10;
string downloadPath = "downloads";
bool useApi = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--tags" && i + 1 < args.Length) tags = args[++i];
    else if (args[i] == "--quantity" && i + 1 < args.Length) quantity = ushort.Parse(args[++i]);
    else if (args[i] == "--path" && i + 1 < args.Length) downloadPath = args[++i];
    else if (args[i] == "--api") useApi = true;
}

if (string.IsNullOrEmpty(tags))
{
    Console.Error.WriteLine("Usage: --tags <tags> [--quantity <n>] [--path <dir>] [--api]");
    return;
}

SettingsModel.Images = true;
SettingsModel.Gif = true;
SettingsModel.Video = true;
SettingsModel.IsApi = useApi;
SettingsModel.Limit = quantity;

Console.WriteLine($"Downloading: tags={tags}, quantity={quantity}, path={downloadPath}, api={useApi}");

var prog = new Progress<int>(p => Console.WriteLine($"Progress: {p}/{quantity}"));

if (useApi)
    R34ApiService.DownloadContent(downloadPath, tags, quantity, prog, prog);
else
    R34HtmlService.DownloadContent(downloadPath, tags, quantity, prog, prog);

Console.WriteLine("✅ Done.");
