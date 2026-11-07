#!/usr/bin/env python3
"""Web茶まめ API のサンプルプログラム

テキストファイルを Web茶まめ API で形態素解析し、結果を CSV（または TSV）に保存します。
Python 3.8 以降の標準ライブラリだけで動きます（追加のインストールは不要です）。

使い方の例:
    python chamame_api.py input.txt
    python chamame_api.py input.txt -o result.csv --dic 中古和文 --options odoriji
    python chamame_api.py input.txt --items f1,f2,f3 --dic-version ver.230922 --tsv
    python chamame_api.py sjis.txt --encoding cp932

自分のプログラムから使う場合:
    from chamame_api import analyze
    result = analyze("今日は晴れ。", outputitems=["f1", "f3"])
    for row in result["rows"]:
        print(row)

API の詳しい説明: https://chamame.ninjal.ac.jp/api.html
"""

import argparse
import csv
import gzip
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

API_URL = "https://chamame.ninjal.ac.jp/chamamebin/API/request.php"

# 1回に送る文字数の目安。API の上限は10万字なので、余裕を持たせて5万字ずつ送る
CHUNK_CHARS = 50000

# 混み合っているとき（429・503）に送り直す回数の上限
MAX_RETRIES = 10


class ChamameAPIError(Exception):
    """API がエラーを返したときの例外"""


def _post(payload):
    """API に1回リクエストを送り、結果（result の中身）を返す。制限に達したときは待って送り直す。"""
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    for attempt in range(MAX_RETRIES + 1):
        req = urllib.request.Request(
            API_URL,
            data=body,
            headers={
                "Content-Type": "application/json",
                "Accept-Encoding": "gzip",  # 結果を圧縮して受け取る
                "User-Agent": "chamame-api-sample/1.0",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=300) as res:
                data = res.read()
                if res.headers.get("Content-Encoding") == "gzip":
                    data = gzip.decompress(data)
                return json.loads(data.decode("utf-8"))["result"]
        except urllib.error.HTTPError as e:
            data = e.read()
            if e.headers.get("Content-Encoding") == "gzip":
                data = gzip.decompress(data)
            try:
                message = json.loads(data.decode("utf-8")).get("error", "")
            except ValueError:
                message = data.decode("utf-8", "replace")[:200]
            # 回数・同時リクエストの上限や混雑のときは、指示された秒数だけ待って送り直す
            if e.code in (429, 503) and attempt < MAX_RETRIES:
                wait = int(e.headers.get("Retry-After", "5"))
                print(f"  {message}。{wait}秒待って送り直します…", file=sys.stderr)
                time.sleep(wait)
                continue
            raise ChamameAPIError(f"HTTP {e.code}: {message}") from None
    raise ChamameAPIError("混み合っているため、送り直しの上限に達しました")


def split_text(text, chunk_chars=CHUNK_CHARS):
    """テキストを、行の区切りで chunk_chars 字以内のかたまりに分ける。

    API は1行を1文として解析するので、行の途中で切らなければ解析結果は変わらない。
    1行だけで chunk_chars 字を超える場合は、「。」の後で切る（それもなければ文字数で切る）。
    """
    chunks, current, size = [], [], 0
    # 改行（\r\n、\r、\n）だけで行に分け、改行文字は前の行に含めたままにする。
    # （str.splitlines() は改ページ \f などでも分けてしまい、API の行の扱いと合わないため使わない）
    for line in re.findall(r"[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$", text):
        while len(line) > chunk_chars:
            cut = line.rfind("。", 0, chunk_chars) + 1 or chunk_chars
            if current:
                chunks.append("".join(current))
                current, size = [], 0
            chunks.append(line[:cut])
            line = line[cut:]
        if size + len(line) > chunk_chars and current:
            chunks.append("".join(current))
            current, size = [], 0
        current.append(line)
        size += len(line)
    if current:
        chunks.append("".join(current))
    # 空白だけのかたまりは送らない（API がエラーを返すため）
    return [c for c in chunks if c.strip()]


def analyze(text, dic="gendai", dic_version="ver.251218", outputitems=None, options=None,
            chunk_chars=CHUNK_CHARS, progress=False):
    """テキストを形態素解析する。長いテキストは分けて送り、結果を1つにまとめて返す。

    引数:
        text: 解析するテキスト
        dic: 辞書の ID または日本語名（例: "gendai"、"中古和文"）
        dic_version: 辞書の版（例: "ver.251218"）
        outputitems: 出力項目のキーまたは名前のリスト（例: ["f1", "f3"]）。None なら API の初期設定
        options: 解析前処理のキーのリスト（例: ["odoriji", "kaigyo"]）
        progress: True なら進み具合を表示する

    戻り値:
        API の result と同じ形の辞書（dic, dic_version, columns, column_keys, rows）
    """
    payload = {"dic": dic, "dic_version": dic_version}
    if outputitems:
        payload["outputitems"] = {key: True for key in outputitems}
    if options:
        payload["options"] = {key: True for key in options}

    chunks = split_text(text, chunk_chars)
    merged = None
    for i, chunk in enumerate(chunks, 1):
        if progress:
            print(f"解析中 {i}/{len(chunks)}（{len(chunk)}字）", file=sys.stderr)
        result = _post({**payload, "txt": chunk})
        if merged is None:
            merged = result
        else:
            merged["rows"].extend(result["rows"])
    if merged is None:
        raise ChamameAPIError("解析するテキストがありません")
    return merged


def save(result, path, tsv=False):
    """解析結果を CSV（Excel で開ける BOM 付き UTF-8）または TSV（UTF-8）で保存する。"""
    if tsv:
        # 解析結果の値にはタブや改行が含まれないので、そのままタブでつなぐ
        with open(path, "w", encoding="utf-8", newline="") as f:
            for row in [result["columns"]] + result["rows"]:
                f.write("\t".join(row) + "\n")
    else:
        with open(path, "w", encoding="utf-8-sig", newline="") as f:
            writer = csv.writer(f)
            writer.writerow(result["columns"])
            writer.writerows(result["rows"])


def main():
    parser = argparse.ArgumentParser(description="Web茶まめ API でテキストファイルを形態素解析する")
    parser.add_argument("input", help="解析するテキストファイル")
    parser.add_argument("-o", "--output", help="結果を保存するファイル（省略すると 入力ファイル名.csv）")
    parser.add_argument("--dic", default="gendai", help="辞書の ID または日本語名（既定: gendai＝現代語）")
    parser.add_argument("--dic-version", default="ver.251218", help="辞書の版（既定: ver.251218）")
    parser.add_argument("--items", help="出力項目をカンマ区切りで指定（例: f1,f2,f3）。省略すると API の初期設定")
    parser.add_argument("--options", help="解析前処理をカンマ区切りで指定（例: odoriji,kaigyo）")
    parser.add_argument("--encoding", default="utf-8", help="入力ファイルの文字コード（既定: utf-8。Shift_JIS なら cp932）")
    parser.add_argument("--tsv", action="store_true", help="CSV ではなく TSV で保存する")
    args = parser.parse_args()

    with open(args.input, encoding=args.encoding) as f:
        text = f.read()
    # 入力ファイル名の拡張子を .csv（TSV なら .tsv）に替える
    output = args.output or os.path.splitext(args.input)[0] + (".tsv" if args.tsv else ".csv")

    def split_list(s):
        return [x.strip() for x in s.split(",") if x.strip()] if s else None

    try:
        result = analyze(text, dic=args.dic, dic_version=args.dic_version,
                         outputitems=split_list(args.items), options=split_list(args.options),
                         progress=True)
    except ChamameAPIError as e:
        sys.exit(f"エラー: {e}")
    save(result, output, tsv=args.tsv)
    print(f"{len(result['rows'])}語を {output} に保存しました"
          f"（辞書: {result['dic']}、版: {result['dic_version']}）", file=sys.stderr)


if __name__ == "__main__":
    main()
