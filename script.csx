#!/usr/bin/env dotnet-script
#r "nuget: HtmlAgilityPack, 1.11.46"
#r "nuget: System.IO.Compression, 4.3.0"

using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Linq;
using System.Collections.Generic;
using HtmlAgilityPack;

int wantedImages = 0, wantedGifs = 0, wantedVideos = 0;
string tags = null, outputDir = "downloads";
bool useApi = true, compressLargeFiles = true;
int maxTotalPosts = 3000;
long maxFileSize = 50 * 1024 * 1024; // 50 MB
int videoMinDuration = 0; // ثانیه

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
        case "--no-compress": compressLargeFiles = false; break;
        case "--max-total": maxTotalPosts = int.Parse(args[++i]); break;
        case "--video-min-duration": videoMinDuration = int.Parse(args[++i]); break;
    }
}

if (string.IsNullOrWhiteSpace(tags) || (wantedImages + wantedGifs + wantedVideos == 0))
{
    Console.Error.WriteLine("Usage: dotnet script script.csx --tags <tags> [--images n] [--gifs n] [--videos n] [--video-min-duration sec] [--no-api] ...");
    return 1;
}

string tagFolderName = Regex.Replace(tags, @"[^\w\-]", "_").Trim('_');
string baseTagDir = Path.Combine(outputDir, tagFolderName);

Console.WriteLine($"🚀 Starting: tags='{tags}' | images={wantedImages}, gifs={wantedGifs}, videos={wantedVideos}");
Console.WriteLine($"📁 Output: {Path.GetFullPath(baseTagDir)} | Mode: {(useApi ? "API" : "HTML")}");
Console.WriteLine($"⚙️ Compression: {(compressLargeFiles ? "ON" : "OFF")}");
if (videoMinDuration > 0)
    Console.WriteLine($"🎬 Filtering videos >= {videoMinDuration} sec");
else
    Console.WriteLine($"🎬 No video duration filter");

HttpClient CreateClient()
{
    var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
    handler.CookieContainer.Add(new Cookie { Name = "gdpr", Value = "1", Domain = "rule34.xxx", Path = "/" });
    handler.CookieContainer.Add(new Cookie { Name = "gdpr-consent", Value = "1", Domain = "rule34.xxx", Path = "/" });
    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");
    client.Timeout = TimeSpan.FromSeconds(30);
    return client;
}

async Task<double> GetVideoDurationAsync(string postId)
{
    try
    {
        string apiUrl = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&id={postId}";
        using var client = CreateClient();
        var xml = await client.GetStringAsync(apiUrl);
        XmlDocument doc = new XmlDocument();
        doc.LoadXml(xml);
        var post = doc.SelectSingleNode("//post");
        if (post != null)
        {
            var durStr = post.Attributes?["duration"]?.Value;
            if (double.TryParse(durStr, out double dur)) return dur;
        }
    }
    catch { }
    return -1;
}

async Task<bool> CompressIfNeeded(string filePath)
{
    if (!compressLargeFiles) return false;
    var fileInfo = new FileInfo(filePath);
    if (fileInfo.Length <= maxFileSize) return false;
    
    string zipPath = filePath + ".zip";
    try
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(filePath, Path.GetFileName(filePath), CompressionLevel.Optimal);
        Console.WriteLine($"  📦 Compressed {Path.GetFileName(filePath)} ({fileInfo.Length/1024/1024:F1}MB → {new FileInfo(zipPath).Length/1024/1024:F1}MB)");
        File.Delete(filePath);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ⚠️ Compression failed: {ex.Message}");
        return false;
    }
}

async Task<bool> DownloadFileAsync(string url, string savePath, string type)
{
    if (File.Exists(savePath) || File.Exists(savePath + ".zip")) return false;
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
            
            await CompressIfNeeded(savePath);
            Console.WriteLine($"  ✅ {type}: {Path.GetFileName(savePath)}");
            return true;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            Console.WriteLine($"  ❌ File not found (404): {url}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️ Retry {retry+1}: {ex.Message}");
            await Task.Delay(2000 * (retry + 1));
        }
    }
    return false;
}

// ============== API MODE ==============
async Task ApiMode()
{
    int dlImages = 0, dlGifs = 0, dlVideos = 0;
    int pid = 0;
    int totalChecked = 0;
    
    while ((dlImages < wantedImages || dlGifs < wantedGifs || dlVideos < wantedVideos) && 
           totalChecked < maxTotalPosts)
    {
        string apiUrl = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&tags={Uri.EscapeDataString(tags).Replace("%20","+")}&pid={pid}&limit=100";
        XmlDocument doc = new XmlDocument();
        try
        {
            using var client = CreateClient();
            var xml = await client.GetStringAsync(apiUrl);
            doc.LoadXml(xml);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ API error: {ex.Message}");
            break;
        }

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
            bool downloaded = false;
            
            // اولویت: ویدیو
            if (dlVideos < wantedVideos && (ext == ".mp4" || ext == ".webm"))
            {
                if (videoMinDuration > 0)
                {
                    var durStr = post.Attributes?["duration"]?.Value;
                    if (!double.TryParse(durStr, out double dur) || dur < videoMinDuration)
                    {
                        totalChecked++;
                        continue;
                    }
                }
                downloaded = await DownloadFileAsync(fileUrl, 
                    Path.Combine(baseTagDir, "Video", fileName), "Video");
                if (downloaded) dlVideos++;
            }
            // گیف
            else if (dlGifs < wantedGifs && ext == ".gif")
            {
                downloaded = await DownloadFileAsync(fileUrl, 
                    Path.Combine(baseTagDir, "Gif", fileName), "Gif");
                if (downloaded) dlGifs++;
            }
            // تصویر
            else if (dlImages < wantedImages && !string.IsNullOrEmpty(ext) && 
                     ext != ".swf" && ext != ".zip" && ext != ".mp4" && ext != ".webm" && ext != ".gif")
            {
                downloaded = await DownloadFileAsync(fileUrl, 
                    Path.Combine(baseTagDir, "Images", fileName), "Image");
                if (downloaded) dlImages++;
            }
            
            totalChecked++;
            if (downloaded) await Task.Delay(100);
            
            if (totalChecked % 25 == 0)
                Console.WriteLine($"📊 Checked: {totalChecked} | I:{dlImages}/{wantedImages} G:{dlGifs}/{wantedGifs} V:{dlVideos}/{wantedVideos}");
        }
        pid++;
    }
    Console.WriteLine($"🎉 Done. Checked {totalChecked} posts | Images:{dlImages}/{wantedImages} Gifs:{dlGifs}/{wantedGifs} Videos:{dlVideos}/{wantedVideos}");
}

// ============== HTML MODE (بهبودیافته) ==============
async Task HtmlMode()
{
    int dlImages = 0, dlGifs = 0, dlVideos = 0;
    int pid = 0;
    int totalChecked = 0;
    string baseUrl = "https://rule34.xxx/index.php?page=post&s=list&tags=" + Uri.EscapeDataString(tags).Replace("%20", "+");

    var web = new HtmlWeb()
    {
        PreRequest = request =>
        {
            request.UserAgent = "Mozilla/5.0";
            request.Referer = "https://rule34.xxx/";
            request.Host = "rule34.xxx";
            request.CookieContainer = new CookieContainer();
            request.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
            request.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));
            request.Timeout = 30000;
            return true;
        }
    };

    while ((dlImages < wantedImages || dlGifs < wantedGifs || dlVideos < wantedVideos) && 
           totalChecked < maxTotalPosts)
    {
        HtmlDocument listDoc;
        try { listDoc = web.Load($"{baseUrl}&pid={pid}"); }
        catch { break; }
        
        var thumbs = listDoc.DocumentNode.SelectNodes("//span[@class='thumb']/a");
        if (thumbs == null || thumbs.Count == 0) break;

        var postUrls = thumbs.Select(a => "https://rule34.xxx/" + a.GetAttributeValue("href", "").Replace("&amp;", "&"))
                             .Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

        foreach (var postUrl in postUrls)
        {
            if (dlImages >= wantedImages && dlGifs >= wantedGifs && dlVideos >= wantedVideos) break;
            
            HtmlDocument postDoc;
            try { postDoc = web.Load(postUrl); }
            catch { await Task.Delay(500); continue; }
            
            bool downloaded = false;
            string postIdMatch = Regex.Match(postUrl, @"id=(\d+)").Groups[1].Value;
            
            // ---- ویدیو ----
            if (dlVideos < wantedVideos)
            {
                // 1. ویدیو با تگ <video>
                var videoSrc = postDoc.DocumentNode.SelectSingleNode("//video/source")?.GetAttributeValue("src", null);
                // 2. og:video
                if (string.IsNullOrEmpty(videoSrc))
                    videoSrc = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:video']")?.GetAttributeValue("content", null);
                // 3. لینک مستقیم در صفحه (مثل download link)
                if (string.IsNullOrEmpty(videoSrc))
                {
                    var downloadLink = postDoc.DocumentNode.SelectSingleNode("//a[contains(@href,'.mp4') or contains(@href,'.webm')]");
                    videoSrc = downloadLink?.GetAttributeValue("href", null);
                }
                
                if (!string.IsNullOrEmpty(videoSrc))
                {
                    if (videoSrc.Contains('?')) videoSrc = videoSrc.Substring(0, videoSrc.IndexOf('?'));
                    
                    // فیلتر duration اگر لازم باشد
                    if (videoMinDuration > 0 && !string.IsNullOrEmpty(postIdMatch))
                    {
                        double duration = await GetVideoDurationAsync(postIdMatch);
                        if (duration >= 0 && duration < videoMinDuration)
                        {
                            totalChecked++;
                            continue;
                        }
                    }
                    
                    string fname = Path.GetFileName(videoSrc);
                    downloaded = await DownloadFileAsync(videoSrc, Path.Combine(baseTagDir, "Video", fname), "Video");
                    if (downloaded) dlVideos++;
                }
            }
            
            // ---- گیف (فقط از og:image با پسوند gif) ----
            if (!downloaded && dlGifs < wantedGifs)
            {
                var ogImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (ogImage != null)
                {
                    var imgUrl = ogImage.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(imgUrl))
                    {
                        if (imgUrl.Contains('?')) imgUrl = imgUrl.Substring(0, imgUrl.IndexOf('?'));
                        if (Path.GetExtension(imgUrl)?.ToLowerInvariant() == ".gif")
                        {
                            downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Gif", Path.GetFileName(imgUrl)), "Gif");
                            if (downloaded) dlGifs++;
                        }
                    }
                }
            }
            
            // ---- تصویر ----
            if (!downloaded && dlImages < wantedImages)
            {
                var ogImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (ogImage != null)
                {
                    var imgUrl = ogImage.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(imgUrl))
                    {
                        if (imgUrl.Contains('?')) imgUrl = imgUrl.Substring(0, imgUrl.IndexOf('?'));
                        string ext = Path.GetExtension(imgUrl)?.ToLowerInvariant() ?? ".jpg";
                        if (ext != ".gif")
                        {
                            downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Images", Path.GetFileName(imgUrl)), "Image");
                            if (downloaded) dlImages++;
                        }
                    }
                }
            }
            
            totalChecked++;
            if (downloaded) await Task.Delay(100);
            
            if (totalChecked % 10 == 0)
                Console.WriteLine($"📊 Checked: {totalChecked} | I:{dlImages}/{wantedImages} G:{dlGifs}/{wantedGifs} V:{dlVideos}/{wantedVideos}");
        }
        pid += 42; // هر صفحه 42 پست دارد
    }
    Console.WriteLine($"🎉 Done. Checked {totalChecked} posts | Images:{dlImages}/{wantedImages} Gifs:{dlGifs}/{wantedGifs} Videos:{dlVideos}/{wantedVideos}");
}

if (useApi)
    await ApiMode();
else
    await HtmlMode();

return 0;
