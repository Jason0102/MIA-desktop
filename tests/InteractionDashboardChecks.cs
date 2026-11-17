using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Mia;
using Mia.Services;

internal static class InteractionDashboardChecks
{
    internal static void Run(string scratch, Action<bool, string> check) =>
        VerifyAsync(Path.Combine(scratch, "dashboard"), check).GetAwaiter().GetResult();

    private static async Task VerifyAsync(string path, Action<bool, string> check)
    {
        var memory = new InteractionMemory(path, "fixture-user");
        string generation = memory.Generation();
        const string unsafeTopic = "<script>alert(1)</script>";
        var now = DateTimeOffset.UtcNow;
        memory.Record(new("chat-1", now, "PC", "chat", Topics: [unsafeTopic]));
        memory.Record(new("discord-1", now, "Discord", "chat", Topics: ["Discord 話題"]));
        memory.Record(new("trace-1", now, "PC", "expression", Detail: "[softly] 語音與動畫"));
        Type type = typeof(App).Assembly.GetType("Mia.InteractionDashboard", throwOnError: true)!;
        using var server = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [memory], culture: null)!;
        var url = (Uri)type.GetProperty("Address", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        check(IPAddress.Loopback.ToString() == url.Host, "dashboard listens on IPv4 loopback");

        using var page = await client.GetAsync(url + "?period=today&channel=PC");
        string html = await page.Content.ReadAsStringAsync();
        check(page.IsSuccessStatusCode && html.Contains("今日小記") && !html.Contains("Discord 話題") &&
            html.Contains("[softly] 語音與動畫"), "dashboard filters shows PC narrative and expression trace");
        check(!html.Contains(unsafeTopic) && html.Contains("&lt;script&gt;") &&
            page.Headers.CacheControl?.NoStore == true && page.Headers.Contains("Content-Security-Policy"),
            "dashboard escapes stored text and discourages browser caching and scripting");
        using var wrongPath = await client.GetAsync(new Uri(url, "/"));
        check(wrongPath.StatusCode == HttpStatusCode.NotFound, "dashboard cannot be loaded without its random path");
        using var rebound = new HttpRequestMessage(HttpMethod.Get, url);
        rebound.Headers.Host = "not-local.test";
        using var reboundResponse = await client.SendAsync(rebound);
        check(reboundResponse.StatusCode == HttpStatusCode.NotFound, "dashboard rejects an unrelated Host header");

        using var hostile = await Post("https://not-local.test", ("action", "configure"), ("enabled", "off"), ("days", "10"));
        check(hostile.StatusCode == HttpStatusCode.BadRequest && memory.GetPolicy().Enabled,
            "dashboard rejects writes from a foreign origin");
        string origin = url.GetLeftPart(UriPartial.Authority);
        using var rename = await Post(origin, ("action", "rename"), ("topic", unsafeTopic), ("replacement", "更正名稱"));
        check(rename.StatusCode == HttpStatusCode.SeeOther && memory.Read().Any(item => item.Topics?.Contains("更正名稱") == true),
            "dashboard rename form updates the stored topic");
        using var exclude = await Post(origin, ("action", "exclude"), ("topic", "更正名稱"));
        check(exclude.StatusCode == HttpStatusCode.SeeOther && !memory.Read().Any(item => item.Topics?.Contains("更正名稱") == true),
            "dashboard exclusion form scrubs the stored topic");
        using var preference = await Post(origin, ("action", "preference"), ("name", "語音節奏"), ("value", "慢"));
        check(preference.StatusCode == HttpStatusCode.SeeOther && memory.GetPreferences().GetValueOrDefault("語音節奏") == "慢",
            "dashboard preference form saves local changes");
        using var configure = await Post(origin, ("action", "configure"), ("days", "30"));
        check(configure.StatusCode == HttpStatusCode.SeeOther && memory.GetPolicy() == new InteractionPolicy(false, 30),
            "dashboard policy form controls retention and recording");
        memory.Reset();
        using var stale = await Post(origin, ("action", "preference"), ("name", "語音節奏"), ("value", "慢"));
        check(stale.StatusCode == HttpStatusCode.Conflict && memory.GetPreferences().Count == 0,
            "dashboard forms from before reset cannot restore old preferences");

        async Task<HttpResponseMessage> Post(string source, params (string Key, string Value)[] fields)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Origin", source);
            request.Content = new FormUrlEncodedContent(fields.Append((Key: "generation", Value: generation))
                .Select(field => KeyValuePair.Create(field.Key, field.Value)));
            return await client.SendAsync(request);
        }
    }
}
