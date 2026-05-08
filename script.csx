#!/usr/bin/env dotnet-script
#r "nuget: HtmlAgilityPack, 1.11.46"
#r "nuget: System.IO.Compression, 4.3.0"
#r "nuget: System.Text.Json, 8.0.5"

using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

// ============== API MODE (Rule34 API v2) ==============
async Task ApiMode()
{
    int dlImages = 0, dlGifs = 0, dlVideos = 0;
    int pid = 0;
    int totalChecked = 0;
    int limit = 100; // حداکثر 100 پست در هر درخواست
    
    Console.WriteLine("📡 Using Rule34 API (api.rule34.xxx)");
    
    while ((dlImages < wantedImages || dlGifs < wantedGifs || dlVideos < wantedVideos) && 
           totalChecked < maxTotalPosts)
    {
        string encodedTags = Uri.EscapeDataString(tags).Replace("%20", "+");
        string apiUrl = $"https://api.rule34.xxx/index.php?page=dapi&s=post&q=index&tags={encodedTags}&pid={pid}&limit={limit}&json=1";
        
        Console.WriteLine($"🔍 Fetching API page {pid}...");
        
        try
        {
            using var client = CreateClient();
            var jsonResponse = await client.GetStringAsync(apiUrl);
            
            // Parse JSON response
            using JsonDocument doc = JsonDocument.Parse(jsonResponse);
            var posts = doc.RootElement.EnumerateArray();
            
            if (!posts.Any())
            {
                Console.WriteLine("⚠️ No more posts found");
                break;
            }

            foreach (var post in posts)
            {
                if (dlImages >= wantedImages && dlGifs >= wantedGifs && dlVideos >= wantedVideos) break;
                
                // Get post data
                string fileUrl = post.GetProperty("file_url").GetString();
                string sampleUrl = post.GetProperty("sample_url").GetString();
                string previewUrl = post.GetProperty("preview_url").GetString();
                string image = post.GetProperty("image").GetString();
                int id = post.GetProperty("id").GetInt32();
                string tagsString = post.GetProperty("tags").GetString();
                string rating = post.GetProperty("rating").GetString();
                int? duration = post.TryGetProperty("duration", out var durElement) ? durElement.GetInt32() : null;
                int? width = post.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : null;
                int? height = post.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : null;
                
                // Determine file type from URL
                string fileUrlLower = fileUrl?.ToLowerInvariant() ?? "";
                string imageLower = image?.ToLowerInvariant() ?? "";
                bool isVideo = fileUrlLower.EndsWith(".mp4") || fileUrlLower.EndsWith(".webm") || 
                              imageLower.EndsWith(".mp4") || imageLower.EndsWith(".webm");
                bool isGif = fileUrlLower.EndsWith(".gif") || imageLower.EndsWith(".gif");
                bool isImage = !isVideo && !isGif && !string.IsNullOrEmpty(fileUrl);
                
                // Filter video by duration
                if (isVideo && videoMinDuration > 0)
                {
                    if (!duration.HasValue || duration.Value < videoMinDuration)
                    {
                        totalChecked++;
                        continue;
                    }
                }
                
                bool downloaded = false;
                string fileName = $"{id}_{Path.GetFileName(fileUrl ?? image)}";
                
                // Priority: Video
                if (isVideo && dlVideos < wantedVideos)
                {
                    string downloadUrl = fileUrl ?? sampleUrl;
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        downloaded = await DownloadFileAsync(downloadUrl, 
                            Path.Combine(baseTagDir, "Video", fileName), "Video");
                        if (downloaded) dlVideos++;
                    }
                }
                // Gif
                else if (isGif && dlGifs < wantedGifs)
                {
                    string downloadUrl = fileUrl ?? sampleUrl;
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        downloaded = await DownloadFileAsync(downloadUrl, 
                            Path.Combine(baseTagDir, "Gif", fileName), "Gif");
                        if (downloaded) dlGifs++;
                    }
                }
                // Image
                else if (isImage && dlImages < wantedImages)
                {
                    string downloadUrl = fileUrl ?? sampleUrl;
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        downloaded = await DownloadFileAsync(downloadUrl, 
                            Path.Combine(baseTagDir, "Images", fileName), "Image");
                        if (downloaded) dlImages++;
                    }
                }
                
                totalChecked++;
                if (downloaded) await Task.Delay(100);
                
                if (totalChecked % 10 == 0)
                {
                    Console.WriteLine($"📊 Checked: {totalChecked} | I:{dlImages}/{wantedImages} G:{dlGifs}/{wantedGifs} V:{dlVideos}/{wantedVideos}");
                }
            }
            
            pid++;
            // If we got less posts than limit, we're at the end
            if (posts.Count() < limit)
            {
                Console.WriteLine("📭 Reached end of results");
                break;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"⚠️ API request failed: {ex.Message}");
            break;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"⚠️ JSON parsing error: {ex.Message}");
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Unexpected error: {ex.Message}");
            break;
        }
    }
    
    Console.WriteLine($"🎉 Done. Checked {totalChecked} posts | Images:{dlImages}/{wantedImages} Gifs:{dlGifs}/{wantedGifs} Videos:{dlVideos}/{wantedVideos}");
}

// ============== HTML MODE (برای مواقعی که API کار نمی‌کند) ==============
async Task HtmlMode()
{
    Console.WriteLine("🌐 Falling back to HTML mode (slower, less reliable)");
    
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
            
            // ---- Video ----
            if (dlVideos < wantedVideos)
            {
                var videoSrc = postDoc.DocumentNode.SelectSingleNode("//video/source")?.GetAttributeValue("src", null);
                if (string.IsNullOrEmpty(videoSrc))
                    videoSrc = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:video']")?.GetAttributeValue("content", null);
                
                if (!string.IsNullOrEmpty(videoSrc))
                {
                    string fname = Path.GetFileName(videoSrc);
                    downloaded = await DownloadFileAsync(videoSrc, Path.Combine(baseTagDir, "Video", fname), "Video");
                    if (downloaded) dlVideos++;
                }
            }
            
            // ---- Gif ----
            if (!downloaded && dlGifs < wantedGifs)
            {
                var ogImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (ogImage != null)
                {
                    var imgUrl = ogImage.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(imgUrl) && imgUrl.ToLowerInvariant().EndsWith(".gif"))
                    {
                        downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Gif", Path.GetFileName(imgUrl)), "Gif");
                        if (downloaded) dlGifs++;
                    }
                }
            }
            
            // ---- Image ----
            if (!downloaded && dlImages < wantedImages)
            {
                var ogImage = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                if (ogImage != null)
                {
                    var imgUrl = ogImage.GetAttributeValue("content", null);
                    if (!string.IsNullOrEmpty(imgUrl) && !imgUrl.ToLowerInvariant().EndsWith(".gif"))
                    {
                        downloaded = await DownloadFileAsync(imgUrl, Path.Combine(baseTagDir, "Images", Path.GetFileName(imgUrl)), "Image");
                        if (downloaded) dlImages++;
                    }
                }
            }
            
            totalChecked++;
            if (downloaded) await Task.Delay(100);
            
            if (totalChecked % 5 == 0)
                Console.WriteLine($"📊 Checked: {totalChecked} | I:{dlImages}/{wantedImages} G:{dlGifs}/{wantedGifs} V:{dlVideos}/{wantedVideos}");
        }
        pid += 42;
    }
    Console.WriteLine($"🎉 Done. Checked {totalChecked} posts | Images:{dlImages}/{wantedImages} Gifs:{dlGifs}/{wantedGifs} Videos:{dlVideos}/{wantedVideos}");
}

try
{
    if (useApi)
        await ApiMode();
    else
        await HtmlMode();
}
catch (Exception ex)
{
    Console.WriteLine($"💥 Fatal error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    return 1;
}

return 0;
