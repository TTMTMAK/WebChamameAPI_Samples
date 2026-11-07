# Web茶まめ API R サンプル

テキストファイルを Web茶まめ API で形態素解析し、結果を CSV（または TSV）に保存するプログラムです。

- R 4.1 以降で動きます（Windows では R 4.2 以降）。パッケージは `httr2`（1.0.0 以降）と `jsonlite` を使います。
- 長いテキストは、行の区切りで自動的に分けて送ります（1回5万字ずつ）。行の途中では切らないので、まとめて送った場合と同じ結果になります。
- API の利用上の制限（1分あたりの回数など）に達したときは、指示された秒数だけ待ってから自動で送り直します。

API の詳しい説明は [Web茶まめ API 利用ガイド](https://chamame.ninjal.ac.jp/api.html) をご覧ください。

## 準備

R で次を1回だけ実行して、パッケージをインストールします。

```r
install.packages(c("httr2", "jsonlite"))
```

すでに `httr2` が入っている場合は、`packageVersion("httr2")` で 1.0.0 以降か確かめてください。古い場合は、上のコマンドをもう一度実行すると新しくなります。

## 使い方

```bash
# 現代語の辞書で解析し、input.csv に保存する
Rscript chamame_api.R input.txt

# 保存先、辞書、解析前処理を指定する
Rscript chamame_api.R input.txt -o result.csv --dic 中古和文 --options odoriji

# 出力項目と辞書の版を指定し、TSV で保存する
Rscript chamame_api.R input.txt --items f1,f2,f3 --dic-version ver.230922 --tsv

# Shift_JIS のファイルを解析する
Rscript chamame_api.R sjis.txt --encoding cp932
```

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

Windows で `Rscript` が見つからないときは、`"C:\Program Files\R\R-4.x.x\bin\Rscript.exe"` のように、インストールした場所を付けて実行してください。

## 自分のプログラムから使う

`chamame_api.R` を `source()` で読み込んで、`analyze()` を呼び出します（読み込んだだけでは解析は始まりません）。

```r
source("chamame_api.R")

result <- analyze("今日は晴れ。", outputitems = c("f1", "f3"))
df <- as_data_frame(result)
print(df)
```

`analyze()` は、長いテキストを分けて送り、結果を1つにまとめて返します。戻り値は API の `result` と同じ項目を持つリストです（`dic`、`dic_version`、`columns`、`column_keys`、`rows`）。`rows` は文字列の行列で、1行が1語に当たります。

`as_data_frame()` は、結果をデータフレームにします。列名は `column_keys`（`f1` など）です。`as_data_frame(result, "columns")` とすると、日本語の列名（`語彙素` など）になります。

ファイルを読んで解析し、保存までするときは、次のように書けます。

```r
text <- read_text("input.txt", encoding = "utf-8")
result <- analyze(text, dic = "中古和文", progress = TRUE)
save_result(result, "result.csv")
```
