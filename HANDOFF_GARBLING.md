# 引継ぎ: 長行 CRLF 警告の garbling — 解決メモ

ripple のドッグフード中、別 AI から「長い git CRLF 警告 (~100+ chars) が文字化けする」フィードバックが届いたバグの調査・修正記録。

## 報告された症状

```
実際の git 出力:
  warning: in the working copy of 'LilySharp.Core/Rendering/FontStyle.cs',
  LF will be replaced by CRLF the next time Git touches it

ripple 表示:
  - FontSitle.cs        (FontStyle.cs の y → i)
  - IDrawuches itxt.cs  (IDrawingContext.cs のはず)
  - Shareches iter.cs   (SharedRenderer.cs のはず)
  - Pdf/PGit touches itxt.cs  (PdfDrawingContext.cs のはず)
  - 行末も "Git touches i" で切れている
```

別行の文字片が混入しており、`git status --short` の M 行 (短い行) は正常。

## Root Cause (確定)

ConPTY は viewport 右端で長行を wrap するとき、「単純な auto-wrap」ではなく以下のシーケンスを emit する:

```
<chars-up-to-col-Cols> \r\n \e[<viewportBottomRow>;<Cols>H <continuation>
```

`peek_console --raw` で実観測:
```
warning: ...PageBreaker.cs', LF will be replaced by CRLF the n\r\n
\e[24;105H next time Git touches it\r\n
```

つまり ConPTY は:
1. viewport 端まで chars を出力 (col 0..104)
2. `\r\n` で visible row を 1 つ進める (= scroll)
3. `\e[24;105H` (CSI CUP) で cursor を visible row 24 col 105 に位置決め — 視覚的に wrap 続きが書ける位置
4. 残りの chars を書く

CommandOutputRenderer は論理行モデル (MaxCol=100,000) なので、`\r\n` を真の論理改行と解釈 → 続く CUP が次行 col 105 に書き込み、そこに居座っていた prompt echo (`git add -A 2>&1 | Out-String`) と接触して garbling になる。最初の warning だけが汚染されたのは、その時点で visible row 24 に直前のコマンド echo が居座っており、続き文字列がそこに上書きされたため。2件目以降は scroll で被害が出ない位置に着地。

## 別 AI の元レポートで採用された 3 仮説の事後評価

| 仮説 | 結論 |
|------|------|
| #1 VtLite の `CarriageReturn` を soft-wrap 遡上 | **的外れ** (bare CR ではなく CUP) — revert 済 |
| #2 chunk-boundary CR 取りこぼし | **的外れ** (chunk 境界の問題ではない) |
| #3 ConPTY が独自の VT seq で wrap continuation | **これが本命** |

## 修正 (Services/CommandOutputRenderer.cs)

ConPTY の "LF + CUP-back-to-margin" パターンを検出して、CUP のターゲット先ではなく **直前の論理行末** に cursor を戻す:

1. `_viewportCols` フィールドを追加し baseline.Cols から取得
2. `_pendingSoftWrap` / `_lastLfRow` / `_lastLfPreCol` で「直前 LF が wrap 由来かもしれない」状態を追跡
3. `LineFeed()` で pre-LF cursor をキャプチャ。`preLfCol >= _viewportCols - 1` (margin に届いていた) のときだけ flag を立てる
4. CSI H/f (CUP/HVP) で flag が立っていて、target row が現在 row、target col が `>= _viewportCols - 1` なら **redirect**: cursor を `(_lastLfRow, _lastLfPreCol - 1)` に戻す (margin 最終 col)
5. WriteChar / bare CR / `\b` / `\t` / 他の CSI / ESC 7-8 で flag を必ずクリア (LF と target CUP の間に何かが挟まったら無効化)

### redirect 先が `_lastLfPreCol` ではなく `_lastLfPreCol - 1` の理由 (DECAWM 重複)

ConPTY の DECAWM auto-margin は wrap 続きの **先頭文字に prefix 末尾文字を再 emit** する。具体例 (cols=105):

- raw bytes: `...replaced by CRL\r\n\e[24;105HLF the next time...`
- prefix `...CRL` は col 0..104 を埋める (col 104 = `L`)
- continuation の **先頭文字 `L`** は ConPTY が DECAWM 動作で再 emit したもの (= prefix 末尾の `L` と同一文字)

redirect 先を `_lastLfPreCol = 105` (next-write 位置) にすると、col 104 の `L` を残したまま続き先頭 `L` を col 105 に書く → logical 行が `...CRLLF...` になり **LL 重複**。最初の dogfood で発生した garbling (FontSitle.cs ではなく実は CRLLF) の真因。

正しくは redirect 先を `_lastLfPreCol - 1 = 104` にして、続き先頭文字で col 104 を上書き (同一文字なので no-op)、続き 2 文字目以降が col 105〜 に書かれる → 重複なし。

LF で生成された phantom blank row は Render の trim で消えるか、次の本物の line で上書きされる。両ケースで悪さしない。

## 加えた tests (Tests/CommandOutputRendererTests.cs)

1. **single soft-wrap continuation joined** — `0123456789\r\n\e[24;10H9 next part\r\n` → `"0123456789 next part"` (続き先頭 `9` が prefix 末尾と重複する DECAWM 動作)
2. **multi-wrap chain joined** — 2x viewport wrap で各 continuation の先頭が前回末尾と重複
3. **non-margin CUP after LF must not redirect** — TUI の本物 CUP (col 1) は redirect しない
4. **short LF must not arm detector** — preLfCol が margin 未満なら flag 立たない
5. **ConPTY DECAWM duplicate-char wrap** — 実 105-col viewport の git CRLF warning パターンで重複が消えることを確認

全 9/9 pass。既存テスト suite も全 pass。**live verification (git add -A in C:\Temp\crlf-garbling-test) でも `CRLF` 表示で重複なしを確認済。**

## VtLiteState 側

前セッションで入れた `CarriageReturn()` の soft-wrap 遡上は **発火しない / 不要** だったので revert 済。ConPTY が CUP を使うので bare CR で wrap 続きを書く経路は実在しない。

## End-to-End 検証手順

1. `Build.ps1` を実行 → `dist/ripple.exe` 更新 (npm/dist にもコピー)
2. ripple-dev MCP server を再起動 (process kill → Claude Code が次回 tool call で再起動)
3. 再現 sandbox で確認:
   ```pwsh
   cd C:\Temp\crlf-garbling-test
   git rm --cached -rf .
   $names = @('LilySharp.Core/Rendering/FontStyle.cs', ...)  # 元の 10 件
   foreach ($n in $names) { [IO.File]::WriteAllText("$PWD\$n", "namespace Foo;`npublic class Bar { }`n") }
   git add -A 2>&1
   ```
   出力に `git add -A 2>&1 | Out-String` の echo や前行の文字片が混入しないこと、各 warning が独立した 1 行として出ることを確認。
4. peek_console `--raw` でも `\r\n + \e[<row>;<col>H` パターンが visible row 上の wrap 続きとして見える (peek_console = VtLiteState 経由は visual grid 維持なのでもともと OK)。

## 関連ファイル

| 場所 | 責務 |
|------|------|
| `Services/CommandOutputRenderer.cs:144-184` | `_viewportCols`, `_pendingSoftWrap` 等の field |
| `Services/CommandOutputRenderer.cs:520-570` | `LineFeed()` の pre-LF キャプチャ + arm |
| `Services/CommandOutputRenderer.cs:580` | `WriteChar()` の flag クリア |
| `Services/CommandOutputRenderer.cs:898-906` | `ApplyCsi()` 入口で `wasPendingSoftWrap` を保存 + クリア |
| `Services/CommandOutputRenderer.cs:910-960` | CSI H/f case の soft-wrap redirect ロジック |
| `Tests/CommandOutputRendererTests.cs:147-238` | 追加 tests |
| `Services/VtLiteState.cs:209` | `CarriageReturn()` を素朴版に revert 済 |
