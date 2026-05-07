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
int maxTotalPosts = 2000;
long maxFileSize = 50 * 1024 * 1024; // 50 MB (GitHub limit)

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
    }
}

if (string.IsNullOrWhiteSpace(tags) || (wantedImages + wantedGifs + wantedVideos == 0))
{
    Console.Error.WriteLine("Usage: dotnet script script.csx --tags <tags> [--images n] [--gifs n] [--videos n] [--path dir] [--no-api] [--no-compress] [--max-total n]");
    return 1;
}

// Clean tag for folder name
string tagFolderName = Regex.Replace(tags, @"[^\w\-]", "_").Trim('_');
string baseTagDir = Path.Combine(outputDir, tagFolderName);

Console.WriteLine($"🚀 Starting: tags='{tags}' | images={wantedImages}, gifs={wantedGifs}, videos={wantedVideos}");
Console.WriteLine($"📁 Output: {Path.GetFullPath(baseTagDir)} | Mode: {(useApi ? "API" : "HTML")}");
Console.WriteLine($"⚙️ Compression: {(compressLargeFiles ? "ON" : "OFF")}");

HttpClient CreateClient()
{
    var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
    handler.CookieContainer.Add(new Cookie { Name = "gdpr", Value = "1", Domain = "rule34.xxx", Path = "/" });
    handler.CookieContainer.Add(new Cookie { Name = "gdpr-consent", Value = "1", Domain = "rule34.xxx", Path = "/" });
    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");
    client.Timeout = TimeSpan.FromSeconds(30);
    return client;
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
        Console.WriteLine($"  📦 Compressed {Path.GetFileName(filePath)} ({fileInfo.Length / 1024 / 1024}MB → {new FileInfo(zipPath).Length / 1024 / 1024}MB)");
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
            Console.WriteLine($"  ❌ File not found: {url}");
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

// حالت API
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
            
            bool downloaded = false;
            
            // اولویت ۱: ویدیو
            if (dlVideos < wantedVideos && (ext == ".mp4" || ext == ".webm"))
            {
                downloaded = await DownloadFileAsync(fileUrl, 
                    Path.Combine(baseTagDir, "Video", fileName), "Video");
                if (downloaded) dlVideos++;
            }
            // اولویت ۲: گیف
            else if (dlGifs < wantedGifs && ext == ".gif")
            {
                downloaded = await DownloadFileAsync(fileUrl, 
                    Path.Combine(baseTagDir, "Gif", fileName), "Gif");
                if (downloaded) dlGifs++;
            }
            // اولویت ۳: تصاویر (اگر هنوز نیاز داریم)
            else if (dlImages < wantedImages && !string.IsNullOrEmpty(ext) && 
                     ext != ".swf" && ext != ".zip" && ext != ".mp4" && ext != ".webm" && ext != ".gif")
            {
                downloaded = await DownloadFileAsync(fileUrl, 
                    Path.Combine(baseTagDir, "Images", fileName), "Image");
                if (downloaded) dlImages++;
            }
            
            totalChecked++;
            if (downloaded) await Task.Delay(100);
            
            // نمایش وضعیت هر ۱۰ پست
            if (totalChecked % 10 == 0)
            {
                Console.WriteLine($"📊 Checked: {totalChecked} | images {dlImages}/{wantedImages} | gifs {dlGifs}/{wantedGifs} | videos {dlVideos}/{wantedVideos}");
            }
        }
        pid++;
        
        // اگر در این صفحه چیزی پیدا نکردیم، ادامه بدیم
        if (dlImages + dlGifs + dlVideos == 0 && pid > 5)
        {
            Console.WriteLine($"⚠️ No content found for tag '{tags}' after {pid} pages");
            break;
        }
    }
    Console.WriteLine($"🎉 Done. Checked {totalChecked} posts | images: {dlImages}/{wantedImages} | gifs: {dlGifs}/{wantedGifs} | videos: {dlVideos}/{wantedVideos}");
}

// حالت HTML
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
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
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
        try
        {
            listDoc = web.Load($"{baseUrl}&pid={pid}");
        }
        catch { break; }
        
        var thumbs = listDoc.DocumentNode.SelectNodes("//div[@class='content']//span[@class='thumb']/a");
        if (thumbs == null || thumbs.Count == 0) break;

        var postUrls = thumbs.Select(a => "https://rule34.xxx/" + a.GetAttributeValue("href", "").Replace("&amp;", "&"))
                             .Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

        foreach (var postUrl in postUrls)
        {
            if (dlImages >= wantedImages && dlGifs >= wantedGifs && dlVideos >= wantedVideos) break;
            
            HtmlDocument postDoc;
            try
            {
                postDoc = web.Load(postUrl);
            }
            catch
            {
                await Task.Delay(500);
                continue;
            }
            
            bool downloaded = false;
            
            // اولویت ۱: ویدیو
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
                        downloaded = await DownloadFileAsync(vidUrl, 
                            Path.Combine(baseTagDir, "Video", fname), "Video");
                        if (downloaded) dlVideos++;
                        goto NextPost;
                    }
                }
                
                var ogVideo = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:video']");
                if (ogVideo != null)
                {
                    var vidUrl = ogVideo.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(vidUrl))
                    {
                        if (vidUrl.Contains('?')) vidUrl = vidUrl.Substring(0, vidUrl.LastIndexOf('?'));
                        string fname = Path.GetFileName(vidUrl);
                        downloaded = await DownloadFileAsync(vidUrl, 
                            Path.Combine(baseTagDir, "Video", fname), "Video");
                        if (downloaded) dlVideos++;
                        goto NextPost;
                    }
                }
            }
            
            // اولویت ۲: گیف
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
                            downloaded = await DownloadFileAsync(imgUrl, 
                                Path.Combine(baseTagDir, "Gif", fname), "Gif");
                            if (downloaded) dlGifs++;
                            goto NextPost;
                        }
                    }
                }
            }
            
            // اولویت ۳: تصویر
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
                            downloaded = await DownloadFileAsync(imgUrl, 
                                Path.Combine(baseTagDir, "Images", fname), "Image");
                            if (downloaded) dlImages++;
                            goto NextPost;
                        }
                    }
                }
            }
            
        NextPost:
            totalChecked++;
            if (downloaded) await Task.Delay(100);
            
            // نمایش وضعیت هر ۵ پست
            if (totalChecked % 5 == 0)
            {
                Console.WriteLine($"📊 Checked: {totalChecked} | images {dlImages}/{wantedImages} | gifs {dlGifs}/{wantedGifs} | videos {dlVideos}/{wantedVideos}");
            }
        }
        pid += 42;
        
        // اگر در این صفحه چیزی پیدا نکردیم، ادامه بدیم
        if (dlImages + dlGifs + dlVideos == 0 && pid > 210) // 5 pages
        {
            Console.WriteLine($"⚠️ No content found for tag '{tags}' after {pid/42} pages");
            break;
        }
    }
    Console.WriteLine($"🎉 Done. Checked {totalChecked} posts | images: {dlImages}/{wantedImages} | gifs: {dlGifs}/{wantedGifs} | videos: {dlVideos}/{wantedVideos}");
}

if (useApi)
    await ApiMode();
else
    await HtmlMode();

return 0;
