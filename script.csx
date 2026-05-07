#!/usr/bin/env dotnet-script
#r "nuget: HtmlAgilityPack, 1.11.46"

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Linq;
using HtmlAgilityPack;

int wantedImages = 0, wantedGifs = 0, wantedVideos = 0;
string tags = null, outputDir = "downloads";
bool useApi = true;
int maxTotalPosts = 2000;

var args = Args.ToArray();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--tags": tags = args[++i]; break;
        case "--images": wantedImages = int.Parse(args[++i]); break;
        case "--gifs": wantedGifs = int.Parse(args[++i]); break;
        case "--videos": wantedVideos = int.Parse(args[++i]); break;
        case "--path": outputDir = args[++i]; break;
        case "--no-api": useApi = false; break;
        case "--max-total": maxTotalPosts = int.Parse(args[++i]); break;
    }
}

if (string.IsNullOrWhiteSpace(tags) || (wantedImages + wantedGifs + wantedVideos == 0))
{
    Console.Error.WriteLine("Usage: dotnet script script.csx --tags <tags> [--images n] [--gifs n] [--videos n] [--path dir] [--no-api] [--max-total n]");
    return 1;
}

Console.WriteLine($"🚀 Starting: tags='{tags}' | images={wantedImages}, gifs={wantedGifs}, videos={wantedVideos}");
Console.WriteLine($"📁 Output: {Path.GetFullPath(outputDir)} | Mode: {(useApi ? "API" : "HTML")}");

HttpClient CreateClient()
{
    var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
    handler.CookieContainer.Add(new Cookie { Name = "gdpr", Value = "1", Domain = "rule34.xxx", Path = "/" });
    handler.CookieContainer.Add(new Cookie { Name = "gdpr-consent", Value = "1", Domain = "rule34.xxx", Path = "/" });
    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");
    return client;
}

async Task<bool> DownloadFileAsync(string url, string savePath)
{
    if (File.Exists(savePath)) return false;
    Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

    using var client = CreateClient();
    for (int retry = 0; retry < 3; retry++)
    {
        try
        {
            using var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var file = File.Create(savePath);
            await stream.CopyToAsync(file);
            Console.WriteLine($"  ✅ {savePath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️ Retry {retry+1}: {ex.Message}");
            await Task.Delay(2000);
        }
    }
    return false;
}

// ========== API mode ==========
async Task ApiMode()
{
    int dlImages = 0, dlGifs = 0, dlVideos = 0;
    int pid = 0;
    while ((dlImages < wantedImages || dlGifs < wantedGifs || dlVideos < wantedVideos) && pid * 100 < maxTotalPosts)
    {
        string apiUrl = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&tags={Uri.EscapeDataString(tags).Replace("%20","+")}&pid={pid}&limit=100";
        XmlDocument doc = new XmlDocument();
        try
        {
            using var client = CreateClient();
            var xml = await client.GetStringAsync(apiUrl);
            doc.LoadXml(xml);
        }
        catch { break; }

        var posts = doc.GetElementsByTagName("post");
        if (posts.Count == 0) break;

        foreach (XmlNode post in posts)
        {
            if (dlImages >= wantedImages && dlGifs >= wantedGifs && dlVideos >= wantedVideos) break;
            
            var fileUrl = post.Attributes?["file_url"]?.Value;
            if (fileUrl == null) continue;
            
            string ext = Path.GetExtension(fileUrl)?.ToLowerInvariant() ?? "";
            string id = post.Attributes["id"].Value;
            string fileName = $"{id}{ext}";
            
            // تشخیص نوع فایل بر اساس پسوند
            bool downloaded = false;
            
            if (ext == ".mp4" || ext == ".webm")
            {
                if (dlVideos < wantedVideos)
                {
                    downloaded = await DownloadFileAsync(fileUrl, Path.Combine(outputDir, "Video", fileName));
                    if (downloaded) dlVideos++;
                }
            }
            else if (ext == ".gif")
            {
                if (dlGifs < wantedGifs)
                {
                    downloaded = await DownloadFileAsync(fileUrl, Path.Combine(outputDir, "Gif", fileName));
                    if (downloaded) dlGifs++;
                }
            }
            else if (!string.IsNullOrEmpty(ext) && ext != ".swf" && ext != ".zip")
            {
                // تصاویر (jpg, png, etc.)
                if (dlImages < wantedImages)
                {
                    downloaded = await DownloadFileAsync(fileUrl, Path.Combine(outputDir, "Images", fileName));
                    if (downloaded) dlImages++;
                }
            }
            
            await Task.Delay(150);
        }
        pid++;
        Console.WriteLine($"📊 images {dlImages}/{wantedImages} | gifs {dlGifs}/{wantedGifs} | videos {dlVideos}/{wantedVideos}");
    }
    Console.WriteLine($"🎉 Done. images: {dlImages}/{wantedImages} | gifs: {dlGifs}/{wantedGifs} | videos: {dlVideos}/{wantedVideos}");
}

// ========== HTML mode ==========
async Task HtmlMode()
{
    int dlImages = 0, dlGifs = 0, dlVideos = 0;
    int pid = 0;
    string baseUrl = "https://rule34.xxx/index.php?page=post&s=list&tags=" + Uri.EscapeDataString(tags).Replace("%20", "+");
    int maxPages = 50;

    var web = new HtmlWeb()
    {
        PreRequest = request =>
        {
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
            request.Referer = "https://rule34.xxx/";
            request.Host = "rule34.xxx";
            request.CookieContainer = new CookieContainer();
            request.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
            request.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));
            return true;
        }
    };

    while ((dlImages < wantedImages || dlGifs < wantedGifs || dlVideos < wantedVideos) && pid / 42 < maxPages)
    {
        var listDoc = web.Load($"{baseUrl}&pid={pid}");
        var thumbs = listDoc.DocumentNode.SelectNodes("//div[@class='content']//span[@class='thumb']/a");
        if (thumbs == null) break;

        var postUrls = thumbs.Select(a => "https://rule34.xxx/" + a.GetAttributeValue("href", "").Replace("&amp;", "&"))
                             .Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

        foreach (var postUrl in postUrls)
        {
            if (dlImages >= wantedImages && dlGifs >= wantedGifs && dlVideos >= wantedVideos) break;
            
            var postDoc = web.Load(postUrl);
            bool downloaded = false;
            
            // اولویت ۱: بررسی ویدیو
            if (dlVideos < wantedVideos)
            {
                var videoSource = postDoc.DocumentNode.SelectSingleNode("//video[@id='gelcomVideoPlayer']/source");
                if (videoSource != null)
                {
                    var vidUrl = videoSource.GetAttributeValue("src", null);
                    if (!string.IsNullOrEmpty(vidUrl))
                    {
                        if (vidUrl.Contains('?')) vidUrl = vidUrl.Substring(0, vidUrl.LastIndexOf('?'));
                        string fname = Path.GetFileName(vidUrl);
                        downloaded = await DownloadFileAsync(vidUrl, Path.Combine(outputDir, "Video", fname));
                        if (downloaded) dlVideos++;
                        await Task.Delay(150);
                        continue;
                    }
                }
                
                // روش پشتیبان برای ویدیو
                var ogVideo = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:video']");
                if (ogVideo != null)
                {
                    var vidUrl = ogVideo.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(vidUrl))
                    {
                        if (vidUrl.Contains('?')) vidUrl = vidUrl.Substring(0, vidUrl.LastIndexOf('?'));
                        string fname = Path.GetFileName(vidUrl);
                        downloaded = await DownloadFileAsync(vidUrl, Path.Combine(outputDir, "Video", fname));
                        if (downloaded) dlVideos++;
                        await Task.Delay(150);
                        continue;
                    }
                }
            }
            
            // اولویت ۲: بررسی گیف
            if (dlGifs < wantedGifs)
            {
                var ogImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (ogImage != null)
                {
                    var imgUrl = ogImage.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(imgUrl))
                    {
                        if (imgUrl.Contains('?')) imgUrl = imgUrl.Substring(0, imgUrl.LastIndexOf('?'));
                        string ext = Path.GetExtension(imgUrl)?.ToLowerInvariant() ?? ".jpg";
                        
                        if (ext == ".gif")
                        {
                            string fname = Path.GetFileName(imgUrl);
                            downloaded = await DownloadFileAsync(imgUrl, Path.Combine(outputDir, "Gif", fname));
                            if (downloaded) dlGifs++;
                            await Task.Delay(150);
                            continue;
                        }
                    }
                }
            }
            
            // اولویت ۳: بررسی تصویر (اگر نه ویدیو و نه گیف بود)
            if (dlImages < wantedImages)
            {
                var ogImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (ogImage != null)
                {
                    var imgUrl = ogImage.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(imgUrl))
                    {
                        if (imgUrl.Contains('?')) imgUrl = imgUrl.Substring(0, imgUrl.LastIndexOf('?'));
                        string ext = Path.GetExtension(imgUrl)?.ToLowerInvariant() ?? ".jpg";
                        
                        if (ext != ".gif" && !string.IsNullOrEmpty(ext))
                        {
                            string fname = Path.GetFileName(imgUrl);
                            downloaded = await DownloadFileAsync(imgUrl, Path.Combine(outputDir, "Images", fname));
                            if (downloaded) dlImages++;
                            await Task.Delay(150);
                            continue;
                        }
                    }
                }
            }
            
            await Task.Delay(150);
        }
        pid += 42;
        Console.WriteLine($"📊 images {dlImages}/{wantedImages} | gifs {dlGifs}/{wantedGifs} | videos {dlVideos}/{wantedVideos}");
    }
    Console.WriteLine($"🎉 Done. images: {dlImages}/{wantedImages} | gifs: {dlGifs}/{wantedGifs} | videos: {dlVideos}/{wantedVideos}");
}

if (useApi)
    await ApiMode();
else
    await HtmlMode();
