#!/usr/bin/env dotnet-script
#r "nuget: HtmlAgilityPack, 1.11.46"
#r "nuget: System.IO.Compression, 4.3.0"

using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using HtmlAgilityPack;

int wantedImages = 0, wantedGifs = 0, wantedVideos = 0;
string tags = null, outputDir = "downloads";
bool compressLargeFiles = true;
int maxTotalPosts = 3000;
long maxFileSize = 50 * 1024 * 1024;
int videoMinDuration = 0;

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
        case "--no-compress": compressLargeFiles = false; break;
        case "--max-total": maxTotalPosts = int.Parse(args[++i]); break;
        case "--video-min-duration": videoMinDuration = int.Parse(args[++i]); break;
    }
}

if (string.IsNullOrWhiteSpace(tags) || (wantedImages + wantedGifs + wantedVideos == 0))
{
    Console.Error.WriteLine("Usage: dotnet script script.csx --tags <tags> [--images n] [--gifs n] [--videos n] [--video-min-duration sec] ...");
    return 1;
}

string tagFolderName = Regex.Replace(tags, @"[^\w\-]", "_").Trim('_');
string baseTagDir = Path.Combine(outputDir, tagFolderName);

Console.WriteLine($"Starting: tags='{tags}' | images={wantedImages}, gifs={wantedGifs}, videos={wantedVideos}");
Console.WriteLine($"Output: {Path.GetFullPath(baseTagDir)}");
Console.WriteLine($"Mode: HTML Only");
Console.WriteLine($"Compression: {(compressLargeFiles ? "ON" : "OFF")}");
if (videoMinDuration > 0)
    Console.WriteLine($"Filtering videos >= {videoMinDuration} sec");

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
        Console.WriteLine($"  Compressed {Path.GetFileName(filePath)} ({fileInfo.Length/1024/1024:F1}MB → {new FileInfo(zipPath).Length/1024/1024:F1}MB)");
        File.Delete(filePath);
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Compression failed: {ex.Message}");
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
            Console.WriteLine($"  {type}: {Path.GetFileName(savePath)}");
            return true;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            Console.WriteLine($"  File not found (404): {url}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Retry {retry+1}: {ex.Message}");
            await Task.Delay(2000 * (retry + 1));
        }
    }
    return false;
}

async Task HtmlMode()
{
    Console.WriteLine("Running in HTML mode");
    
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
            Console.WriteLine($"Loading page {pid}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load page {pid}: {ex.Message}");
            break;
        }
        
        var thumbs = listDoc.DocumentNode.SelectNodes("//span[@class='thumb']/a");
        if (thumbs == null || thumbs.Count == 0)
        {
            Console.WriteLine("No more posts found");
            break;
        }

        var postUrls = thumbs.Select(a => "https://rule34.xxx/" + a.GetAttributeValue("href", "").Replace("&amp;", "&"))
                             .Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

        Console.WriteLine($"Found {postUrls.Count} posts on page {pid}");

        foreach (var postUrl in postUrls)
        {
            if (dlImages >= wantedImages && dlGifs >= wantedGifs && dlVideos >= wantedVideos) break;
            
            HtmlDocument postDoc;
            try 
            { 
                postDoc = web.Load(postUrl);
                await Task.Delay(200);
            }
            catch 
            { 
                Console.WriteLine($"Failed to load post: {postUrl}");
                continue; 
            }
            
            bool downloaded = false;
            
            if (dlVideos < wantedVideos)
            {
                var videoElement = postDoc.DocumentNode.SelectSingleNode("//video/source[@src]");
                if (videoElement == null)
                    videoElement = postDoc.DocumentNode.SelectSingleNode("//video[@src]");
                if (videoElement == null)
                    videoElement = postDoc.DocumentNode.SelectSingleNode("//a[contains(@href, '.mp4') or contains(@href, '.webm')]");
                
                string videoUrl = null;
                if (videoElement != null)
                {
                    videoUrl = videoElement.GetAttributeValue("src", null) ?? 
                              videoElement.GetAttributeValue("href", null);
                }
                
                if (string.IsNullOrEmpty(videoUrl))
                {
                    var metaVideo = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:video']");
                    if (metaVideo != null)
                        videoUrl = metaVideo.GetAttributeValue("content", null);
                }
                
                if (!string.IsNullOrEmpty(videoUrl))
                {
                    if (videoUrl.StartsWith("//"))
                        videoUrl = "https:" + videoUrl;
                    else if (videoUrl.StartsWith("/"))
                        videoUrl = "https://rule34.xxx" + videoUrl;
                    
                    string fileName = Path.GetFileName(videoUrl.Split('?')[0]);
                    downloaded = await DownloadFileAsync(videoUrl, Path.Combine(baseTagDir, "Video", fileName), "Video");
                    if (downloaded) dlVideos++;
                }
            }
            
            if (!downloaded && dlGifs < wantedGifs)
            {
                var imgElements = postDoc.DocumentNode.SelectNodes("//img[@src]");
                if (imgElements != null)
                {
                    foreach (var img in imgElements)
                    {
                        var imgUrl = img.GetAttributeValue("src", "");
                        if (!string.IsNullOrEmpty(imgUrl) && 
                            (imgUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || 
                             imgUrl.Contains(".gif?")))
                        {
                            if (imgUrl.StartsWith("//"))
                                imgUrl = "https:" + imgUrl;
                            else if (imgUrl.StartsWith("/"))
                                imgUrl = "https://rule34.xxx" + imgUrl;
                            
                            string fileName = Path.GetFileName(imgUrl.Split('?')[0]);
                            downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Gif", fileName), "Gif");
                            if (downloaded) 
                            {
                                dlGifs++;
                                break;
                            }
                        }
                    }
                }
                
                if (!downloaded)
                {
                    var metaImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                    if (metaImage != null)
                    {
                        var imgUrl = metaImage.GetAttributeValue("content", "");
                        if (!string.IsNullOrEmpty(imgUrl) && 
                            imgUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        {
                            string fileName = Path.GetFileName(imgUrl.Split('?')[0]);
                            downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Gif", fileName), "Gif");
                            if (downloaded) dlGifs++;
                        }
                    }
                }
            }
            
            if (!downloaded && dlImages < wantedImages)
            {
                var metaImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (metaImage != null)
                {
                    var imgUrl = metaImage.GetAttributeValue("content", "");
                    if (!string.IsNullOrEmpty(imgUrl) && 
                        !imgUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) &&
                        !imgUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
                        !imgUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                    {
                        if (imgUrl.StartsWith("//"))
                            imgUrl = "https:" + imgUrl;
                        else if (imgUrl.StartsWith("/"))
                            imgUrl = "https://rule34.xxx" + imgUrl;
                        
                        string fileName = Path.GetFileName(imgUrl.Split('?')[0]);
                        downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Images", fileName), "Image");
                        if (downloaded) dlImages++;
                    }
                }
                
                if (!downloaded)
                {
                    var imgElements = postDoc.DocumentNode.SelectNodes("//img[@src]");
                    if (imgElements != null)
                    {
                        foreach (var img in imgElements)
                        {
                            var imgUrl = img.GetAttributeValue("src", "");
                            if (!string.IsNullOrEmpty(imgUrl) && 
                                !imgUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) &&
                                (imgUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                 imgUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                 imgUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                 imgUrl.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (imgUrl.StartsWith("//"))
                                    imgUrl = "https:" + imgUrl;
                                else if (imgUrl.StartsWith("/"))
                                    imgUrl = "https://rule34.xxx" + imgUrl;
                                
                                string fileName = Path.GetFileName(imgUrl.Split('?')[0]);
                                downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Images", fileName), "Image");
                                if (downloaded) 
                                {
                                    dlImages++;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            
            totalChecked++;
            
            if (totalChecked % 10 == 0)
            {
                Console.WriteLine($"Checked: {totalChecked} | I:{dlImages}/{wantedImages} G:{dlGifs}/{wantedGifs} V:{dlVideos}/{wantedVideos}");
            }
            
            if (downloaded)
                await Task.Delay(500);
        }
        
        pid += 42;
        
        if (postUrls.Count > 0)
            await Task.Delay(1000);
    }
    
    Console.WriteLine($"Done. Checked {totalChecked} posts | Images:{dlImages}/{wantedImages} Gifs:{dlGifs}/{wantedGifs} Videos:{dlVideos}/{wantedVideos}");
}

try
{
    await HtmlMode();
}
catch (Exception ex)
{
    Console.WriteLine($"Fatal error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    return 1;
}

return 0;
