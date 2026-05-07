#!/usr/bin/env dotnet-script
#r "nuget: HtmlAgilityPack, 1.11.46"

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Linq;
using HtmlAgilityPack;

// ===================== تنظیمات =====================
int wantedImages = 0;
int wantedGifs   = 0;
int wantedVideos = 0;
string tags      = null;
string outputDir = "downloads";
bool useApi = true;
int maxTotalPosts = 2000;
int maxPagesHTML = 50;

// ===================== پارس آرگومان‌ها =====================
var args = Args.ToArray();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--tags" && i + 1 < args.Length)
        tags = args[++i];
    else if (args[i] == "--images" && i + 1 < args.Length)
        wantedImages = int.Parse(args[++i]);
    else if (args[i] == "--gifs" && i + 1 < args.Length)
        wantedGifs = int.Parse(args[++i]);
    else if (args[i] == "--videos" && i + 1 < args.Length)
        wantedVideos = int.Parse(args[++i]);
    else if (args[i] == "--path" && i + 1 < args.Length)
        outputDir = args[++i];
    else if (args[i] == "--no-api")
        useApi = false;
    else if (args[i] == "--max-total" && i + 1 < args.Length)
        maxTotalPosts = int.Parse(args[++i]);
}

if (string.IsNullOrWhiteSpace(tags) || (wantedImages + wantedGifs + wantedVideos == 0))
{
    Console.Error.WriteLine("Usage: dotnet script script.csx --tags <tags> [--images <n>] [--gifs <n>] [--videos <n>] [--path <dir>] [--no-api] [--max-total <n>]");
    Console.Error.WriteLine("Example (API): dotnet script script.csx --tags 'anime' --images 10 --gifs 10 --videos 10");
    Console.Error.WriteLine("Example (HTML): dotnet script script.csx --tags 'anime' --images 10 --gifs 10 --videos 10 --no-api");
    return 1;
}

Console.WriteLine($"🚀 شروع دانلود: tags='{tags}' | images={wantedImages}, gifs={wantedGifs}, videos={wantedVideos}");
Console.WriteLine($"📁 پوشه خروجی: {Path.GetFullPath(outputDir)}");
Console.WriteLine($"🔧 حالت: {(useApi ? "API" : "HTML (Scraping)")}");

// ===================== HTTP Client =====================
HttpClient CreateHttpClient()
{
    var handler = new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer()
    };
    handler.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
    handler.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));
    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");
    return client;
}

// ===================== دانلود فایل =====================
async Task DownloadFileAsync(string url, string savePath)
{
    if (File.Exists(savePath))
    {
        Console.WriteLine($"  ⏭ فایل وجود دارد: {savePath}");
        return;
    }

    var dir = Path.GetDirectoryName(savePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);

    using var client = CreateHttpClient();
    for (int retry = 0; retry < 3; retry++)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(savePath);
            await stream.CopyToAsync(fileStream);
            Console.WriteLine($"  ✅ دانلود شد: {savePath} ({(int)response.Content.Headers.ContentLength} bytes)");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️ تلاش {retry+1} ناموفق: {ex.Message}");
            await Task.Delay(2000);
        }
    }
    Console.WriteLine($"  ❌ دانلود نهایی ناموفق: {url}");
}

// ===================== ۱. حالت API =====================
async Task DownloadWithApi()
{
    int downloadedImages = 0, downloadedGifs = 0, downloadedVideos = 0;
    int processedPosts = 0;
    int pid = 0;

    while ((downloadedImages < wantedImages || downloadedGifs < wantedGifs || downloadedVideos < wantedVideos)
           && processedPosts < maxTotalPosts)
    {
        string encodedTags = Uri.EscapeDataString(tags).Replace("%20", "+");
        string apiUrl = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&tags={encodedTags}&pid={pid}&limit=100";
        Console.WriteLine($"\n📡 صفحه API {pid}: {apiUrl}");

        using var client = CreateHttpClient();
        string xmlContent;
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(apiUrl);
            Console.WriteLine($"   ↳ کد وضعیت HTTP: {(int)response.StatusCode} ({response.StatusCode})");
            xmlContent = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ خطا در دریافت API: {ex.Message}");
            break;
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"   ❌ API خطا داد. محتوا (۳۰۰ اول): {xmlContent.Substring(0, Math.Min(300, xmlContent.Length))}");
            break;
        }

        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            Console.WriteLine("   ⚠️ پاسخ API خالی است.");
            break;
        }

        Console.WriteLine($"   ↳ طول پاسخ: {xmlContent.Length} کاراکتر");
        XmlDocument doc = new XmlDocument();
        try
        {
            doc.LoadXml(xmlContent);
        }
        catch (XmlException ex)
        {
            Console.WriteLine($"   ❌ XML نامعتبر: {ex.Message}");
            Console.WriteLine($"   ↳ محتوای دریافتی: {xmlContent.Substring(0, Math.Min(300, xmlContent.Length))}");
            break;
        }

        if (doc.DocumentElement == null)
        {
            Console.WriteLine("   ⚠️ سند XML بدون ریشه است.");
            break;
        }

        XmlNodeList posts = doc.DocumentElement.GetElementsByTagName("post");
        Console.WriteLine($"   ↳ تعداد پست در صفحه: {posts.Count}");

        if (posts.Count == 0)
        {
            Console.WriteLine("   🏁 هیچ پستی در این صفحه نیست. جستجو پایان یافت.");
            break;
        }

        foreach (XmlNode node in posts)
        {
            if (downloadedImages >= wantedImages && downloadedGifs >= wantedGifs && downloadedVideos >= wantedVideos)
                break;

            var idAttr = node.Attributes?["id"];
            var fileUrlAttr = node.Attributes?["file_url"];
            if (idAttr == null || fileUrlAttr == null) continue;

            string id = idAttr.Value;
            string fileUrl = fileUrlAttr.Value;
            string extension = Path.GetExtension(fileUrl)?.ToLowerInvariant() ?? "";

            string type = "", subDir = "";
            if ((extension == ".mp4" || extension == ".webm") && downloadedVideos < wantedVideos)
            {
                type = "video"; subDir = "Video";
            }
            else if (extension == ".gif" && downloadedGifs < wantedGifs)
            {
                type = "gif"; subDir = "Gif";
            }
            else if (extension != ".mp4" && extension != ".webm" && extension != ".gif" && downloadedImages < wantedImages)
            {
                type = "image"; subDir = "Images";
            }

            if (!string.IsNullOrEmpty(type))
            {
                string fileName = $"{id}{extension}";
                string savePath = Path.Combine(outputDir, subDir, fileName);
                Console.WriteLine($"⬇ [{type}] {fileUrl}");
                await DownloadFileAsync(fileUrl, savePath);

                if (type == "image") downloadedImages++;
                else if (type == "gif") downloadedGifs++;
                else if (type == "video") downloadedVideos++;
            }

            processedPosts++;
            await Task.Delay(150);
        }

        pid++;
        Console.WriteLine($"📊 پیشرفت کلی: تصاویر {downloadedImages}/{wantedImages} | گیف {downloadedGifs}/{wantedGifs} | ویدیو {downloadedVideos}/{wantedVideos}");
    }

    Console.WriteLine($"\n🎉 پایان. تصاویر: {downloadedImages}/{wantedImages} | گیف: {downloadedGifs}/{wantedGifs} | ویدیو: {downloadedVideos}/{wantedVideos}");
}

// ===================== ۲. حالت HTML =====================
async Task DownloadWithHTML()
{
    int downloadedImages = 0, downloadedGifs = 0, downloadedVideos = 0;
    int pid = 0;
    string baseUrl = "https://rule34.xxx/index.php?page=post&s=list&tags=" + Uri.EscapeDataString(tags).Replace("%20", "+");
    const int pageSize = 42;

    while ((downloadedImages < wantedImages || downloadedGifs < wantedGifs || downloadedVideos < wantedVideos)
           && pid / pageSize < maxPagesHTML)
    {
        string listUrl = $"{baseUrl}&pid={pid}";
        Console.WriteLine($"\n📄 صفحه HTML {pid/pageSize}: {listUrl}");
        
        HtmlDocument listDoc;
        try
        {
            var web = new HtmlWeb
            {
                PreRequest = request =>
                {
                    request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
                    request.Referer = "https://rule34.xxx/";
                    request.Host = "rule34.xxx";
                    request.CookieContainer = new CookieContainer();
                    request.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
                    request.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));
                    return true;
                }
            };
            listDoc = web.Load(listUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ❌ خطا در بارگذاری صفحه: {ex.Message}");
            break;
        }

        var thumbNodes = listDoc.DocumentNode.SelectNodes("//div[@class='content']//span[@class='thumb']/a");
        if (thumbNodes == null || thumbNodes.Count == 0)
        {
            Console.WriteLine("   🏁 هیچ پستی در این صفحه نیست. پایان.");
            break;
        }

        var postUrls = thumbNodes.Select(x => "https://rule34.xxx/" + x.GetAttributeValue("href", "").Replace("&amp;", "&"))
                                 .Where(x => !string.IsNullOrEmpty(x)).ToList();
        Console.WriteLine($"   ↳ یافت {postUrls.Count} پست.");

        foreach (var postUrl in postUrls)
        {
            if (downloadedImages >= wantedImages && downloadedGifs >= wantedGifs && downloadedVideos >= wantedVideos)
                break;

            Console.WriteLine($"🔍 بررسی پست: {postUrl}");
            HtmlDocument postDoc;
            try
            {
                var web = new HtmlWeb
                {
                    PreRequest = request =>
                    {
                        request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
                        request.Referer = "https://rule34.xxx/";
                        request.Host = "rule34.xxx";
                        request.CookieContainer = new CookieContainer();
                        request.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
                        request.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));
                        return true;
                    }
                };
                postDoc = web.Load(postUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ خطا در بارگذاری پست: {ex.Message}");
                continue;
            }

            // ۱. اول og:video را چک کن
            var ogVideoNode = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:video']");
            if (ogVideoNode != null && downloadedVideos < wantedVideos)
            {
                var videoUrl = ogVideoNode.GetAttributeValue("content", null);
                if (!string.IsNullOrEmpty(videoUrl))
                {
                    // حذف query string
                    if (videoUrl.Contains('?'))
                        videoUrl = videoUrl.Substring(0, videoUrl.LastIndexOf('?'));
                    
                    string id = Path.GetFileNameWithoutExtension(videoUrl);
                    string ext = Path.GetExtension(videoUrl)?.ToLowerInvariant() ?? ".mp4";
                    string fileName = $"{id}{ext}";
                    string savePath = Path.Combine(outputDir, "Video", fileName);
                    Console.WriteLine($"⬇ [video] {videoUrl}");
                    await DownloadFileAsync(videoUrl, savePath);
                    downloadedVideos++;
                    await Task.Delay(150);
                    continue; // به پست بعدی برو
                }
            }

            // ۲. اگر ویدیو نبود، og:image را چک کن
            var ogImageNode = postDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            if (ogImageNode != null)
            {
                var imgUrl = ogImageNode.GetAttributeValue("content", null);
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    // حذف query string
                    if (imgUrl.Contains('?'))
                        imgUrl = imgUrl.Substring(0, imgUrl.LastIndexOf('?'));
                    
                    string id = Path.GetFileNameWithoutExtension(imgUrl);
                    string ext = Path.GetExtension(imgUrl)?.ToLowerInvariant() ?? ".jpg";
                    string fileName = $"{id}{ext}";

                    if (ext == ".gif" && downloadedGifs < wantedGifs)
                    {
                        string savePath = Path.Combine(outputDir, "Gif", fileName);
                        Console.WriteLine($"⬇ [gif] {imgUrl}");
                        await DownloadFileAsync(imgUrl, savePath);
                        downloadedGifs++;
                    }
                    else if (downloadedImages < wantedImages)
                    {
                        string savePath = Path.Combine(outputDir, "Images", fileName);
                        Console.WriteLine($"⬇ [image] {imgUrl}");
                        await DownloadFileAsync(imgUrl, savePath);
                        downloadedImages++;
                    }
                    await Task.Delay(150);
                }
            }
            else
            {
                Console.WriteLine("   ⚠️ هیچ og:image یا og:video یافت نشد.");
            }
        }

        pid += pageSize;
        Console.WriteLine($"📊 پیشرفت: تصاویر {downloadedImages}/{wantedImages} | گیف {downloadedGifs}/{wantedGifs} | ویدیو {downloadedVideos}/{wantedVideos}");
    }

    Console.WriteLine($"\n🎉 پایان. تصاویر: {downloadedImages}/{wantedImages} | گیف: {downloadedGifs}/{wantedGifs} | ویدیو: {downloadedVideos}/{wantedVideos}");
}

// ===================== اجرای اصلی =====================
if (useApi)
    await DownloadWithApi();
else
    await DownloadWithHTML();
