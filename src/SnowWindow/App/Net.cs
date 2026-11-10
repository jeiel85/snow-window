using System.Net.Http.Headers;

namespace SnowWindow.App;

/// <summary>업데이트 확인·날씨 조회가 함께 쓰는 HttpClient. 시간 제한은 요청마다 CancellationToken으로 건다.</summary>
internal static class Net
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SnowWindow", Updater.CurrentVersion.ToString()));
        return client;
    }
}
