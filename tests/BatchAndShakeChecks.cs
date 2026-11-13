using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mia;
using Mia.Services;

internal static class BatchAndShakeChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var type=typeof(PetWindow).Assembly.GetType("Mia.WindowShakeDetector")!;
        var detector=Activator.CreateInstance(type,true)!;
        bool Sample(double x,double y,double t)=>(bool)type.GetMethod("Sample")!.Invoke(detector,new object[]{x,y,t})!;
        void Reset()=>type.GetMethod("Reset")!.Invoke(detector,null);
        bool fired=false;for(int i=0;i<50;i++)fired|=Sample(i*12,100,i*.05);
        check(!fired,"steady window movement never shakes pet off");Reset();
        for(int i=0;i<50;i++)fired|=Sample(i%2*10,100,i*.05);
        check(!fired,"small oscillations do not detach");Reset();
        check(!Sample(0,0,0)&&!Sample(120,0,.15)&&!Sample(0,0,.3)&&!Sample(120,0,.45)&&Sample(0,0,.6),"three deliberate fast reversals detach");Reset();
        fired=false;for(int i=0;i<8;i++)fired|=Sample(i%2*120,0,i*.7);
        check(!fired,"slow back and forth stays attached");
        using var ai=new AiService(new AppSettings{OpenAiKey="synthetic-key"},new Handler(check));
        var images=new[]{new byte[]{1,2},new byte[]{3,4},new byte[]{5,6}};
        check(ai.ReplyWithImagesAsync("比較三張圖",images,CancellationToken.None).GetAwaiter().GetResult()=="ok","multi-image reply parsed");
        bool rejected=false;try{ai.ReplyWithImagesAsync("x",Enumerable.Range(0,7).Select(_=>new byte[]{1}).ToArray(),CancellationToken.None).GetAwaiter().GetResult();}catch(ArgumentException){rejected=true;}
        check(rejected,"batch count limit enforced before network");
    }
    private sealed class Handler(Action<bool,string> check):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var content=json.RootElement.GetProperty("input")[2].GetProperty("content").EnumerateArray().ToArray();
            var pictures=content.Where(c=>c.GetProperty("type").GetString()=="input_image").ToArray();
            check(pictures.Length==3,"all three images included in ONE request with text");
            check(content[0].GetProperty("text").GetString()=="比較三張圖","user text preserved alongside batch");
            check(pictures[1].GetProperty("image_url").GetString()!.EndsWith(Convert.ToBase64String(new byte[]{3,4})),"batch image ordering preserved");
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}]}",Encoding.UTF8,"application/json")};
        }
    }
}
