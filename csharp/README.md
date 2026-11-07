# Web茶まめ API C# サンプル

テキストファイルを Web茶まめ API で形態素解析し、結果を CSV（または TSV）に保存するプログラムです。

- .NET 8 以降で動きます。追加のインストールは不要です（標準ライブラリだけを使っています。NuGet パッケージは使いません）。
- 長いテキストは、行の区切りで自動的に分けて送ります（1回5万字ずつ）。行の途中では切らないので、まとめて送った場合と同じ結果になります。
- API の利用上の制限（1分あたりの回数など）に達したときは、指示された秒数だけ待ってから自動で送り直します。

API の詳しい説明は [Web茶まめ API 利用ガイド](https://chamame.ninjal.ac.jp/api.html) をご覧ください。

## 準備

[.NET 8 SDK](https://dotnet.microsoft.com/download) をインストールします。`ChamameApi.csproj` と `Program.cs` を同じフォルダに置き、そのフォルダで次のコマンドを実行します。

`ChamameApi.slnx` は、Visual Studio でこのプログラムを開くためのファイルです（Visual Studio 2022 バージョン 17.13 以降で開けます）。コマンドで実行するだけなら使いません。

## 使い方

```bash
# 現代語の辞書で解析し、input.csv に保存する
dotnet run -- input.txt

# 保存先、辞書、解析前処理を指定する
dotnet run -- input.txt -o result.csv --dic 中古和文 --options odoriji

# 出力項目と辞書の版を指定し、TSV で保存する
dotnet run -- input.txt --items f1,f2,f3 --dic-version ver.230922 --tsv

# Shift_JIS のファイルを解析する
dotnet run -- sjis.txt --encoding cp932
```

`--` の後ろに書いたものが、このプログラムへの指定になります。

| オプション | 内容 |
|---|---|
| `-o`, `--output` | 結果を保存するファイル。省略すると「入力ファイル名.csv」 |
| `--dic` | 辞書の ID または日本語名（既定: `gendai`＝現代語） |
| `--dic-version` | 辞書の版（既定: `ver.251218`） |
| `--items` | 出力項目をカンマ区切りで指定（例: `f1,f2,f3`、`語彙素,品詞`）。省略すると API の初期設定 |
| `--options` | 解析前処理をカンマ区切りで指定（例: `odoriji,kaigyo`） |
| `--encoding` | 入力ファイルの文字コード（既定: `utf-8`。Shift_JIS なら `cp932`） |
| `--tsv` | CSV ではなく TSV で保存する |

CSV は Excel でそのまま開ける形式（BOM付き UTF-8）で保存します。TSV は BOM なしの UTF-8 です。

何度も使うときは、`dotnet build` でビルドしておくと、`bin/Debug/net8.0/` にできる `ChamameApi.exe`（macOS・Linux では `ChamameApi`）を直接実行できます。

## 自分のプログラムから使う

`Program.cs` の中の `ChamameApi` クラスと `ChamameResult` クラスを、自分のプロジェクトにコピーして使います（`Program` クラスはコマンドとして実行するための部分なので、コピーしなくてかまいません）。

```csharp
using Chamame;

using var api = new ChamameApi();
ChamameResult result = await api.AnalyzeAsync("今日は晴れ。", outputItems: ["f1", "f3"]);
foreach (string[] row in result.Rows)
{
    var word = result.ColumnKeys.Zip(row).ToDictionary(p => p.First, p => p.Second);
    Console.WriteLine(string.Join(", ", word));
}
```

`AnalyzeAsync()` は、長いテキストを分けて送り、結果を1つにまとめて返します。戻り値の形は API の `result` と同じです（`Dic`、`DicVersion`、`Columns`、`ColumnKeys`、`Rows`）。`result.Save("result.csv")` で、コマンドと同じ形式で保存できます。
