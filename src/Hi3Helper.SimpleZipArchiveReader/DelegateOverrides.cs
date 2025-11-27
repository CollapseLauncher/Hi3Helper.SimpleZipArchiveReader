using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.SimpleZipArchiveReader;

public static class DelegateOverrides
{
    public delegate ValueTask<long> GetUrlFileLengthAsyncDelegate(HttpClient client, Uri url, CancellationToken token);
    public delegate Task<Stream> GetStreamFromPosAsyncDelegate(HttpClient        client,
                                                               Uri               url,
                                                               long?             offset,
                                                               long?             length,
                                                               CancellationToken token);

    public delegate byte[] GetHeapArrayDelegate(int size);

    public static GetUrlFileLengthAsyncDelegate GetUrlFileLengthAsync = ThisGetUrlFileLengthAsync;
    public static GetStreamFromPosAsyncDelegate GetStreamFromPosAsync = ThisGetStreamFromPosAsync;
    public static GetHeapArrayDelegate          GetHeapArray          = ThisGetHeapArray;

    private static async ValueTask<long> ThisGetUrlFileLengthAsync(
        HttpClient        client,
        Uri               url,
        CancellationToken token)
    {
        using HttpRequestMessage requestMessage = new(HttpMethod.Get, url);
        using HttpResponseMessage responseMessage =
            await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, token);

        responseMessage.EnsureSuccessStatusCode();

        return responseMessage.Content.Headers.ContentRange.Length ?? 0;
    }

    private static async Task<Stream> ThisGetStreamFromPosAsync(
        HttpClient        client,
        Uri               url,
        long?             offset,
        long?             length,
        CancellationToken token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(offset, offset + length);

        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            return await response.Content.ReadAsStreamAsync();
        }
        finally
        {
            if (!(response?.IsSuccessStatusCode ?? false))
            {
                request.Dispose();
                response?.Dispose();
            }
        }
    }

    private static byte[] ThisGetHeapArray(int size) => new byte[size];
}
