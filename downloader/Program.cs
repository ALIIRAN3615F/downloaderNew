using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;

class Program
{
    const string ApiUrl = "https://rule34.xxx/index.php?page=dapi&s=post&q=index";
    const int PageSize = 100;

    static async Task Main(string[] args)
    {
        string tags = Environment.GetEnvironmentVariable("TAGS") ?? "catgirl";
        int limit = int.Parse(Environment.GetEnvironmentVariable("LIMIT") ?? "10");

        string root = Path.Combine(Directory.GetCurrentDirectory(), "..", "Downloads");

        Directory.CreateDirectory(Path.Combine(root, "Images"));
        Directory.CreateDirectory(Path.Combine(root, "Gif"));
        Directory.CreateDirectory(Path.Combine(root, "Video"));

        Console.WriteLine($"Tags: {tags}");
        Console.WriteLine($"Limit: {limit}");

        int pages = (limit / PageSize) + 1;

        for (int pid = 0; pid < pages; pid++)
        {
            var doc = new XmlDocument();
            doc.Load($"{ApiUrl}&tags={tags}&pid={pid}");

            var posts = doc.DocumentElement.ChildNodes;

            foreach (XmlNode node in posts)
            {
                if (limit <= 0) break;

                string url = node.Attributes["file_url"]?.Value;
                string id = node.Attributes["id"]?.Value;

                if (url == null) continue;

                string ext = Path.GetExtension(url);
                string path = "";

                if (ext == ".gif")
                    path = Path.Combine(root, "Gif", id + ext);
                else if (ext == ".mp4" || ext == ".webm")
                    path = Path.Combine(root, "Video", id + ext);
                else
                    path = Path.Combine(root, "Images", id + ext);

                await Download(url, path);

                limit--;
                if (limit <= 0) break;
            }
        }
    }

    static async Task Download(string url, string path)
    {
        if (File.Exists(path)) return;

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        handler.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
        handler.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));

        using var client = new HttpClient(handler);

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");

        try
        {
            Console.WriteLine("Downloading " + Path.GetFileName(path));

            var data = await client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, data);
        }
        catch { }
    }
}
