// Web茶まめ API のサンプルプログラム（C# 版）
//
// テキストファイルを Web茶まめ API で形態素解析し、結果を CSV（または TSV）に保存します。
// .NET 8 以降の標準ライブラリだけで動きます（NuGet パッケージは使いません）。
//
// 使い方の例:
//   dotnet run -- input.txt
//   dotnet run -- input.txt -o result.csv --dic 中古和文 --options odoriji
//   dotnet run -- input.txt --items f1,f2,f3 --dic-version ver.230922 --tsv
//   dotnet run -- sjis.txt --encoding cp932
//
// 自分のプログラムから使う場合（下の ChamameApi クラスと ChamameResult クラスを使います）:
//   using var api = new ChamameApi();
//   ChamameResult result = await api.AnalyzeAsync("今日は晴れ。", outputItems: ["f1", "f3"]);
//   foreach (string[] row in result.Rows)
//       Console.WriteLine(string.Join(", ", row));
//
// API の詳しい説明: https://chamame.ninjal.ac.jp/api.html

using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Chamame;

/// <summary>API がエラーを返したときの例外</summary>
public class ChamameApiException(string message) : Exception(message);

/// <summary>解析結果（API の result と同じ形）</summary>
public class ChamameResult
{
    /// <summary>辞書の名前（例: 現代語）</summary>
    [JsonPropertyName("dic")] public string Dic { get; set; } = "";

    /// <summary>辞書の版（例: 251218）</summary>
    [JsonPropertyName("dic_version")] public string DicVersion { get; set; } = "";

    /// <summary>列の名前（例: 文境界、書字形（＝表層形）、語彙素、品詞）</summary>
    [JsonPropertyName("columns")] public List<string> Columns { get; set; } = [];

    /// <summary>列のキー（例: boundary、surface、f1、f3）</summary>
    [JsonPropertyName("column_keys")] public List<string> ColumnKeys { get; set; } = [];

    /// <summary>解析結果。1つの要素が1語で、値は Columns と同じ順に並ぶ</summary>
    [JsonPropertyName("rows")] public List<string[]> Rows { get; set; } = [];

    /// <summary>解析結果を CSV（Excel で開ける BOM 付き UTF-8）または TSV（UTF-8）で保存する。</summary>
    public void Save(string path, bool tsv = false)
    {
        if (tsv)
        {
            // 解析結果の値にはタブや改行が含まれないので、そのままタブでつなぐ
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
            writer.WriteLine(string.Join("\t", Columns));
            foreach (string[] row in Rows)
                writer.WriteLine(string.Join("\t", row));
        }
        else
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true)) { NewLine = "\r\n" };
            writer.WriteLine(string.Join(",", Columns.Select(QuoteCsv)));
            foreach (string[] row in Rows)
                writer.WriteLine(string.Join(",", row.Select(QuoteCsv)));
        }
    }

    // カンマ・引用符・改行を含む値は "" で囲む（中の " は "" にする）
    private static string QuoteCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

/// <summary>Web茶まめ API を呼び出すクラス</summary>
public sealed class ChamameApi : IDisposable
{
    public const string ApiUrl = "https://chamame.ninjal.ac.jp/chamamebin/API/request.php";

    /// <summary>1回に送る文字数の目安。API の上限は10万字なので、余裕を持たせて5万字ずつ送る</summary>
    public const int ChunkChars = 50000;

    /// <summary>混み合っているとき（429・503）に送り直す回数の上限</summary>
    public const int MaxRetries = 10;

    // 日本語を \uXXXX にせず、そのまま JSON に書く
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly HttpClient _http;

    public ChamameApi()
    {
        // 結果を gzip で圧縮して受け取り、自動で展開する（Accept-Encoding: gzip が付く）
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(300) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("chamame-api-sample/1.0");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// テキストを形態素解析する。長いテキストは分けて送り、結果を1つにまとめて返す。
    /// </summary>
    /// <param name="text">解析するテキスト</param>
    /// <param name="dic">辞書の ID または日本語名（例: "gendai"、"中古和文"）</param>
    /// <param name="dicVersion">辞書の版（例: "ver.251218"）</param>
    /// <param name="outputItems">出力項目のキーまたは名前（例: ["f1", "f3"]）。null なら API の初期設定</param>
    /// <param name="options">解析前処理のキー（例: ["odoriji", "kaigyo"]）</param>
    /// <param name="chunkChars">1回に送る文字数の目安</param>
    /// <param name="progress">true なら進み具合を表示する</param>
    /// <returns>API の result と同じ形の結果（Dic, DicVersion, Columns, ColumnKeys, Rows）</returns>
    public async Task<ChamameResult> AnalyzeAsync(
        string text, string dic = "gendai", string dicVersion = "ver.251218",
        IEnumerable<string>? outputItems = null, IEnumerable<string>? options = null,
        int chunkChars = ChunkChars, bool progress = false, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject { ["dic"] = dic, ["dic_version"] = dicVersion };
        if (outputItems != null && outputItems.Any())
            payload["outputitems"] = ToFlags(outputItems);
        if (options != null && options.Any())
            payload["options"] = ToFlags(options);

        List<string> chunks = SplitText(text, chunkChars);
        ChamameResult? merged = null;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (progress)
                Console.Error.WriteLine($"解析中 {i + 1}/{chunks.Count}（{CountChars(chunks[i])}字）");
            payload["txt"] = chunks[i];
            ChamameResult result = await PostAsync(payload, cancellationToken);
            if (merged == null)
                merged = result;
            else
                merged.Rows.AddRange(result.Rows);
        }
        return merged ?? throw new ChamameApiException("解析するテキストがありません");
    }

    // ["f1", "f3"] を {"f1": true, "f3": true} にする
    private static JsonObject ToFlags(IEnumerable<string> keys)
    {
        var obj = new JsonObject();
        foreach (string key in keys)
            obj[key] = true;
        return obj;
    }

    /// <summary>API に1回リクエストを送り、結果（result の中身）を返す。制限に達したときは待って送り直す。</summary>
    private async Task<ChamameResult> PostAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        string body = payload.ToJsonString(JsonOptions);
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http.PostAsync(ApiUrl, content, cancellationToken);
            byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            string json = Encoding.UTF8.GetString(data);
            if (response.IsSuccessStatusCode)
            {
                var parsed = JsonSerializer.Deserialize<ResponseBody>(json);
                return parsed?.Result ?? throw new ChamameApiException("API の応答を読み取れませんでした");
            }

            string message = ReadErrorMessage(json);
            int code = (int)response.StatusCode;
            // 回数・同時リクエストの上限や混雑のときは、指示された秒数だけ待って送り直す
            if ((code == 429 || code == 503) && attempt < MaxRetries)
            {
                int wait = (int)(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5);
                Console.Error.WriteLine($"  {message}。{wait}秒待って送り直します…");
                await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
                continue;
            }
            throw new ChamameApiException($"HTTP {code}: {message}");
        }
        throw new ChamameApiException("混み合っているため、送り直しの上限に達しました");
    }

    // エラーの応答 {"error": "メッセージ"} からメッセージを取り出す（JSON でなければ先頭の200字）
    private static string ReadErrorMessage(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return json.Length > 200 ? json[..200] : json;
        }
    }

    private class ResponseBody
    {
        [JsonPropertyName("result")] public ChamameResult? Result { get; set; }
    }

    /// <summary>
    /// テキストを、行の区切りで chunkChars 字以内のかたまりに分ける。
    /// API は1行を1文として解析するので、行の途中で切らなければ解析結果は変わらない。
    /// 1行だけで chunkChars 字を超える場合は、「。」の後で切る（それもなければ文字数で切る）。
    /// </summary>
    public static List<string> SplitText(string text, int chunkChars = ChunkChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();  // 作りかけのかたまり
        int size = 0;                       // 作りかけのかたまりの字数
        foreach (string whole in SplitLines(text))
        {
            string line = whole;
            while (CountChars(line) > chunkChars)
            {
                if (size > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                    size = 0;
                }
                int cut = FindCut(line, chunkChars);
                chunks.Add(line[..cut]);
                line = line[cut..];
            }
            // この行を足すと上限を超えるなら、その前までを1つのかたまりにする
            int n = CountChars(line);
            if (size > 0 && size + n > chunkChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
                size = 0;
            }
            current.Append(line);
            size += n;
        }
        if (size > 0)
            chunks.Add(current.ToString());
        // 空白だけのかたまりは送らない（API がエラーを返すため）
        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    // テキストを行に分ける（改行文字 \r\n・\n・\r は前の行に含めたまま）
    private static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || text[i] == '\r')
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    // 1行が長すぎるときに切る位置を返す。
    // 先頭から limit 字以内の最後の「。」の後で切る。「。」がなければ limit 字で切る。
    private static int FindCut(string line, int limit)
    {
        int i = 0, count = 0, afterMaru = -1;
        while (i < line.Length && count < limit)
        {
            if (line[i] == '。')
                afterMaru = i + 1;
            i += char.IsSurrogatePair(line, i) ? 2 : 1;
            count++;
        }
        return afterMaru > 0 ? afterMaru : i;
    }

    /// <summary>
    /// 字数を数える。C# の string.Length は UTF-16 の単位で数えるので、
    /// 「𠮷」のような字が2字と数えられてしまう。ここでは1字として数える。
    /// </summary>
    public static int CountChars(string s)
    {
        int n = 0;
        foreach (Rune _ in s.EnumerateRunes())
            n++;
        return n;
    }
}

/// <summary>コマンドとして実行する部分</summary>
internal static class Program
{
    private const string Usage = """
        使い方: dotnet run -- input [-o OUTPUT] [--dic DIC] [--dic-version VERSION]
                              [--items ITEMS] [--options OPTIONS] [--encoding ENCODING] [--tsv]

        Web茶まめ API でテキストファイルを形態素解析する

          input                解析するテキストファイル
          -o, --output         結果を保存するファイル（省略すると 入力ファイル名.csv）
          --dic                辞書の ID または日本語名（既定: gendai＝現代語）
          --dic-version        辞書の版（既定: ver.251218）
          --items              出力項目をカンマ区切りで指定（例: f1,f2,f3）。省略すると API の初期設定
          --options            解析前処理をカンマ区切りで指定（例: odoriji,kaigyo）
          --encoding           入力ファイルの文字コード（既定: utf-8。Shift_JIS なら cp932）
          --tsv                CSV ではなく TSV で保存する
        """;

    private static async Task<int> Main(string[] args)
    {
        // Windows のコンソールで日本語が文字化けしないようにする
        Console.OutputEncoding = Encoding.UTF8;
        // Shift_JIS（cp932）などを読めるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // コマンドの引数を読み取る
        string? input = null, output = null, items = null, options = null;
        string dic = "gendai", dicVersion = "ver.251218", encoding = "utf-8";
        bool tsv = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? value = null;
            int eq = arg.IndexOf('=');
            if (arg.StartsWith("--") && eq > 0)  // --dic=中古和文 の形
            {
                value = arg[(eq + 1)..];
                arg = arg[..eq];
            }
            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                case "--tsv":
                    tsv = true;
                    continue;
                case "-o" or "--output" or "--dic" or "--dic-version" or "--items" or "--options" or "--encoding":
                    if (value == null)
                    {
                        if (i + 1 >= args.Length)
                            return UsageError($"{arg} の後に値を指定してください");
                        value = args[++i];
                    }
                    switch (arg)
                    {
                        case "-o" or "--output": output = value; break;
                        case "--dic": dic = value; break;
                        case "--dic-version": dicVersion = value; break;
                        case "--items": items = value; break;
                        case "--options": options = value; break;
                        case "--encoding": encoding = value; break;
                    }
                    continue;
            }
            if (arg.StartsWith('-'))
                return UsageError($"知らないオプションです: {arg}");
            if (input != null)
                return UsageError($"入力ファイルは1つだけ指定してください: {arg}");
            input = arg;
        }
        if (input == null)
            return UsageError("解析するテキストファイルを指定してください");

        // 入力ファイル名の拡張子を .csv（TSV なら .tsv）に替える
        output ??= Path.ChangeExtension(input, tsv ? ".tsv" : ".csv");

        try
        {
            string text = ReadText(input, encoding);
            using var api = new ChamameApi();
            ChamameResult result = await api.AnalyzeAsync(
                text, dic, dicVersion, SplitList(items), SplitList(options), progress: true);
            result.Save(output, tsv);
            Console.Error.WriteLine(
                $"{result.Rows.Count}語を {output} に保存しました（辞書: {result.Dic}、版: {result.DicVersion}）");
            return 0;
        }
        // タイムアウトや通信エラーは .NET の英語のメッセージになるので、日本語の説明を付ける
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("エラー: API の応答がありませんでした（タイムアウト）。時間をおいてもう一度試してください");
            return 1;
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"エラー: API に接続できませんでした（{e.Message}）。ネットワークの接続を確かめてください");
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"エラー: {e.Message}");
            return 1;
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(Usage);
        Console.Error.WriteLine($"エラー: {message}");
        return 2;
    }

    // "f1, f3" を ["f1", "f3"] にする
    private static List<string>? SplitList(string? s) =>
        s?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    // テキストファイルを読む（改行は "\n" にそろえる）
    private static string ReadText(string path, string encodingName)
    {
        // "cp932" のような名前は、番号（932）で文字コードを選ぶ
        Encoding encoding = encodingName.StartsWith("cp", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(encodingName[2..], out int codePage)
            ? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            : Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        string text;
        try
        {
            text = encoding.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException)
        {
            throw new IOException($"{path} を文字コード {encodingName} として読めません（--encoding を確かめてください）");
        }
        if (text.StartsWith('\uFEFF'))
            text = text[1..];  // 先頭の BOM（U+FEFF）を除く
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
