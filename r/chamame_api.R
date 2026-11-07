# Web茶まめ API のサンプルプログラム（R 版）
#
# テキストファイルを Web茶まめ API で形態素解析し、結果を CSV（または TSV）に保存します。
# R 4.1 以降（Windows では R 4.2 以降）で動きます。httr2（1.0.0 以降）と jsonlite のパッケージを使います。
#   install.packages(c("httr2", "jsonlite"))
#
# 使い方の例:
#   Rscript chamame_api.R input.txt
#   Rscript chamame_api.R input.txt -o result.csv --dic 中古和文 --options odoriji
#   Rscript chamame_api.R input.txt --items f1,f2,f3 --dic-version ver.230922 --tsv
#   Rscript chamame_api.R sjis.txt --encoding cp932
#
# 自分のプログラムから使う場合:
#   source("chamame_api.R")
#   result <- analyze("今日は晴れ。", outputitems = c("f1", "f3"))
#   df <- as_data_frame(result)
#   print(df)
#
# API の詳しい説明: https://chamame.ninjal.ac.jp/api.html

API_URL <- "https://chamame.ninjal.ac.jp/chamamebin/API/request.php"

# 1回に送る文字数の目安。API の上限は10万字なので、余裕を持たせて5万字ずつ送る
CHUNK_CHARS <- 50000

# 混み合っているとき（429・503）に送り直す回数の上限
MAX_RETRIES <- 10


# API がエラーを返したときのエラー（tryCatch の chamame_api_error で受け取れる）
chamame_api_error <- function(message) {
  structure(class = c("chamame_api_error", "error", "condition"),
            list(message = message, call = NULL))
}


# API に1回リクエストを送り、結果（result の中身）を返す。制限に達したときは待って送り直す。
post_request <- function(payload) {
  body <- charToRaw(enc2utf8(jsonlite::toJSON(payload, auto_unbox = TRUE)))
  req <- httr2::request(API_URL) |>
    httr2::req_body_raw(body, type = "application/json") |>
    httr2::req_options(accept_encoding = "gzip") |>  # 結果を圧縮して受け取る（展開は自動）
    httr2::req_user_agent("chamame-api-sample/1.0") |>
    httr2::req_timeout(300) |>
    httr2::req_error(is_error = function(resp) FALSE)  # エラーの応答も自分で調べる

  for (attempt in 0:MAX_RETRIES) {
    resp <- httr2::req_perform(req)
    status <- httr2::resp_status(resp)
    text <- httr2::resp_body_string(resp, encoding = "UTF-8")
    if (status == 200) {
      return(jsonlite::fromJSON(text, simplifyVector = FALSE)$result)
    }
    msg <- tryCatch(jsonlite::fromJSON(text)$error,
                    error = function(e) substr(text, 1, 200))
    if (is.null(msg)) msg <- ""
    # 回数・同時リクエストの上限や混雑のときは、指示された秒数だけ待って送り直す
    if (status %in% c(429, 503) && attempt < MAX_RETRIES) {
      wait <- suppressWarnings(as.integer(httr2::resp_header(resp, "Retry-After", "5")))
      if (is.na(wait)) wait <- 5
      message(sprintf("  %s。%d秒待って送り直します…", msg, wait))
      Sys.sleep(wait)
      next
    }
    stop(chamame_api_error(sprintf("HTTP %d: %s", status, msg)))
  }
  stop(chamame_api_error("混み合っているため、送り直しの上限に達しました"))
}


# テキストを、行の区切りで chunk_chars 字以内のかたまりに分ける。
#
# API は1行を1文として解析するので、行の途中で切らなければ解析結果は変わらない。
# 1行だけで chunk_chars 字を超える場合は、「。」の後で切る（それもなければ文字数で切る）。
split_text <- function(text, chunk_chars = CHUNK_CHARS) {
  # 1字ずつの番号（Unicode のコードポイント）の並びにして、何字目かという位置で扱う。
  # （長いテキストを substr() などで何度も切り出すと、とても遅くなるため）
  cp <- utf8ToInt(enc2utf8(text))
  # 各行の最後の字の位置（改行文字 \n、\r\n、\r は前の行に含める）
  lone_cr <- cp == 13L & c(cp[-1], 0L) != 10L
  ends <- which(cp == 10L | lone_cr)
  if (length(cp) > 0 && !(length(cp) %in% ends)) {
    ends <- c(ends, length(cp))  # 末尾に改行がない最後の行
  }
  piece <- function(from, to) intToUtf8(cp[from:to])

  chunks <- character(0)  # できあがったかたまり
  start <- 1              # 作りかけのかたまりの始まりの位置
  pos <- 1                # 次の行の始まりの位置（start から pos - 1 までが作りかけ）
  for (end in ends) {
    while (end - pos + 1 > chunk_chars) {
      if (pos > start) chunks <- c(chunks, piece(start, pos - 1))
      # 上限以内の最後の「。」（番号 0x3002）の後で切る。「。」がなければ上限の字数で切る
      maru <- which(cp[pos:(pos + chunk_chars - 1)] == 0x3002L)
      cut <- if (length(maru) > 0) max(maru) else chunk_chars
      chunks <- c(chunks, piece(pos, pos + cut - 1))
      pos <- pos + cut
      start <- pos
    }
    # この行を足すと上限を超えるなら、その前までを1つのかたまりにする
    if (pos > start && end - start + 1 > chunk_chars) {
      chunks <- c(chunks, piece(start, pos - 1))
      start <- pos
    }
    pos <- end + 1
  }
  if (pos > start) chunks <- c(chunks, piece(start, pos - 1))
  # 空白だけのかたまりは送らない（API がエラーを返すため）
  chunks[grepl("[^\\h\\v]", chunks, perl = TRUE)]
}


# テキストを形態素解析する。長いテキストは分けて送り、結果を1つにまとめて返す。
#
# 引数:
#   text: 解析するテキスト
#   dic: 辞書の ID または日本語名（例: "gendai"、"中古和文"）
#   dic_version: 辞書の版（例: "ver.251218"）
#   outputitems: 出力項目のキーまたは名前のベクトル（例: c("f1", "f3")）。NULL なら API の初期設定
#   options: 解析前処理のキーのベクトル（例: c("odoriji", "kaigyo")）
#   progress: TRUE なら進み具合を表示する
#
# 戻り値:
#   API の result と同じ項目を持つリスト（dic, dic_version, columns, column_keys, rows）。
#   rows は文字列の行列で、1行が1語、1列が1項目（columns の順）に当たる。
analyze <- function(text, dic = "gendai", dic_version = "ver.251218", outputitems = NULL,
                    options = NULL, chunk_chars = CHUNK_CHARS, progress = FALSE) {
  payload <- list(dic = dic, dic_version = dic_version)
  if (length(outputitems) > 0) {
    payload$outputitems <- as.list(setNames(rep(TRUE, length(outputitems)), outputitems))
  }
  if (length(options) > 0) {
    payload$options <- as.list(setNames(rep(TRUE, length(options)), options))
  }

  chunks <- split_text(text, chunk_chars)
  if (length(chunks) == 0) {
    stop(chamame_api_error("解析するテキストがありません"))
  }
  merged <- NULL
  rows <- list()
  for (i in seq_along(chunks)) {
    if (progress) {
      message(sprintf("解析中 %d/%d（%d字）", i, length(chunks), nchar(chunks[i])))
    }
    result <- post_request(c(payload, list(txt = chunks[i])))
    if (is.null(merged)) merged <- result
    rows[[i]] <- unlist(result$rows, use.names = FALSE)
  }
  # 各行の値を、1行1語の行列にまとめる
  # （解析結果が0語のとき unlist() は NULL になり matrix() がエラーになるので、as.character() で空の文字列ベクトルにする）
  merged$columns <- unlist(merged$columns)
  merged$column_keys <- unlist(merged$column_keys)
  merged$rows <- matrix(as.character(unlist(rows)), ncol = length(merged$columns), byrow = TRUE)
  merged
}


# 解析結果をデータフレームにする。
# 列名は column_keys（"f1" など）。col_names = "columns" なら日本語の列名（"語彙素" など）にする。
as_data_frame <- function(result, col_names = c("column_keys", "columns")) {
  col_names <- match.arg(col_names)
  df <- as.data.frame(result$rows, stringsAsFactors = FALSE)
  names(df) <- result[[col_names]]
  df
}


# 解析結果を CSV（Excel で開ける BOM 付き UTF-8）または TSV（UTF-8）で保存する。
save_result <- function(result, path, tsv = FALSE) {
  table <- rbind(result$columns, result$rows)
  if (tsv) {
    # 解析結果の値にはタブや改行が含まれないので、そのままタブでつなぐ
    lines <- apply(table, 1, paste, collapse = "\t")
    bytes <- charToRaw(enc2utf8(paste0(lines, "\n", collapse = "")))
  } else {
    # カンマ・引用符・改行を含む値は "" で囲む（中の " は "" にする）
    quote <- grepl('[",\r\n]', table)
    table[quote] <- paste0('"', gsub('"', '""', table[quote], fixed = TRUE), '"')
    lines <- apply(table, 1, paste, collapse = ",")
    bytes <- c(as.raw(c(0xEF, 0xBB, 0xBF)),  # BOM
               charToRaw(enc2utf8(paste0(lines, "\r\n", collapse = ""))))
  }
  writeBin(bytes, path)
}


# テキストファイルを読み、UTF-8 の文字列にする（改行は "\n" にそろえる）
read_text <- function(path, encoding = "utf-8") {
  bytes <- readBin(path, "raw", file.size(path))
  text <- iconv(list(bytes), from = encoding, to = "UTF-8")
  if (is.na(text)) {
    stop(sprintf("%s を文字コード %s として読めません（--encoding を確かめてください）",
                 path, encoding))
  }
  text <- sub("^\uFEFF", "", text)  # 先頭の BOM（U+FEFF）を除く
  gsub("\r\n?", "\n", text)
}


USAGE <- "使い方: Rscript chamame_api.R input [-o OUTPUT] [--dic DIC] [--dic-version VERSION]
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
"


# コマンドの引数を読み取る
parse_args <- function(args) {
  usage_error <- function(msg) {
    message(USAGE)
    message("エラー: ", msg)
    quit(status = 2)
  }
  # 値をとるオプションと、それを入れる名前
  with_value <- c("-o" = "output", "--output" = "output", "--dic" = "dic",
                  "--dic-version" = "dic_version", "--items" = "items",
                  "--options" = "options", "--encoding" = "encoding")
  opts <- list(input = NULL, output = NULL, dic = "gendai", dic_version = "ver.251218",
               items = NULL, options = NULL, encoding = "utf-8", tsv = FALSE)
  i <- 1
  while (i <= length(args)) {
    arg <- args[i]
    value <- NULL
    if (grepl("^--[^=]+=", arg)) {  # --dic=中古和文 の形
      value <- sub("^[^=]+=", "", arg)
      arg <- sub("=.*$", "", arg)
    }
    if (arg %in% c("-h", "--help")) {
      cat(USAGE)
      quit(status = 0)
    } else if (arg == "--tsv") {
      opts$tsv <- TRUE
    } else if (arg %in% names(with_value)) {
      if (is.null(value)) {
        if (i == length(args)) usage_error(paste0(arg, " の後に値を指定してください"))
        i <- i + 1
        value <- args[i]
      }
      opts[[with_value[[arg]]]] <- value
    } else if (startsWith(arg, "-")) {
      usage_error(paste0("知らないオプションです: ", arg))
    } else if (is.null(opts$input)) {
      opts$input <- arg
    } else {
      usage_error(paste0("入力ファイルは1つだけ指定してください: ", arg))
    }
    i <- i + 1
  }
  if (is.null(opts$input)) usage_error("解析するテキストファイルを指定してください")
  opts
}


main <- function() {
  args <- parse_args(enc2utf8(commandArgs(trailingOnly = TRUE)))
  output <- args$output
  if (is.null(output)) {
    # 入力ファイル名の拡張子を .csv（TSV なら .tsv）に替える
    output <- paste0(sub("\\.[^./\\\\]*$", "", args$input), if (args$tsv) ".tsv" else ".csv")
  }

  split_list <- function(s) {
    if (is.null(s)) return(NULL)
    x <- trimws(strsplit(s, ",", fixed = TRUE)[[1]])
    x[x != ""]
  }

  result <- tryCatch({
    text <- read_text(args$input, args$encoding)
    analyze(text, dic = args$dic, dic_version = args$dic_version,
            outputitems = split_list(args$items), options = split_list(args$options),
            progress = TRUE)
  }, error = function(e) {
    message("エラー: ", conditionMessage(e))
    quit(status = 1)
  })
  save_result(result, output, tsv = args$tsv)
  message(sprintf("%d語を %s に保存しました（辞書: %s、版: %s）",
                  nrow(result$rows), output, result$dic, result$dic_version))
}


# Rscript で実行したときだけ main() を動かす（source() で読み込んだときは関数を定義するだけ）
if (sys.nframe() == 0) {
  main()
}
