# GeminiGatewayClient 探索的テスト観点仕様書
(GeminiGatewayClient Exploratory Testing Perspectives & Test Plan)

**ドキュメントバージョン**: 1.0.0  
**対象プロジェクト**: `GeminiGatewayClient`  
**検証対象テストプロジェクト**: `tests/GeminiGatewayClient.ExploratoryTests`  
**関連プロジェクト**: `tests/GeminiGatewayClient.Tests` (単体テスト 185 件)

---

## 1. 探索的テストの目的と検証方針

### 1.1 探索的テストの目的と位置づけ

`GeminiGatewayClient` は、Google の Gemini REST API および Microsoft の統一 AI 抽象インターフェース `Microsoft.Extensions.AI` (MEAI) を高効率・ステートレスに利用するための .NET 10 ライブラリです。

本ソリューションにおけるテスト体系は、以下の 2 段階で構成されています：

1. **単体テスト (`tests/GeminiGatewayClient.Tests`)**:
   - `MockHttpMessageHandler` 等を用いた決定論的なテスト（全 185 件）。
   - クライアントの電文シリアライズ、ヘッダー付与、URL 構築、SSE/JSON Array パーサーの境界値、メモリ割り当てなどを高速・オフラインに検証。
2. **探索的テスト (`tests/GeminiGatewayClient.ExploratoryTests`)**:
   - 実際の Google Gemini API エンドポイント（デフォルト: `gemini-3-flash-preview` または `gemini-2.5-flash`）に対して直接 HTTP リクエストを発行し、実 API の生きた挙動とクライアントの統合性を確認するライブ検証スイート。

#### 探索的テストが果たす中核的価値
- **実 API のプロトコル互換性保証**:
  モックでは見落とされがちな、Google API サーバー側の厳格なバリデーション（例: パラメータなし関数定義で空オブジェクト `{}` を送ると HTTP 400 が発生する仕様や、Thought Signature の整合性検証、非オブジェクト形式の関数戻り値の拒絶など）を実環境で検知・保証します。
- **LLM の非決定的な振る舞いに対する堅牢性検証**:
  LLM はプロンプトや文脈に応じて、単一ターンで並列に関数を呼び出したり、推論（Thinking）を行ったり、ツールエラーに対して代替手段を試みるなど、動的な振る舞いを示します。これらが高レイヤーの抽象化（特に MEAI の `FunctionInvokingChatClient`）を介して期待通りハンドリングされるかをエンドツーエンドで実証します。
- **不変条件（Invariants）に基づく検証方針**:
  実 LLM の出力テキストは確率的で変動するため、完全一致比較（Verbatim Assertions）は行いません。「ツール実行トレースに必要な関数呼び出しがすべて含まれているか」「返却された JSON が指定スキーマに合致しているか」「エラー時に例外でクラッシュせずモデルが代替テキストを返しているか」など、意味論的・構造的な**不変条件**をアサーション対象とします。

---

### 1.2 実行制御と環境変数 `GeminiAPIKey` のスキップ仕様

探索的テストは実際の外部 API クォータとネットワークを消費するため、CI パイプラインや API キーを持たない外部開発者の環境でテストスイート全体がエラーとなってはなりません。これを実現するため、独自属性による自動スキップと並行実行制御を実装しています。

#### 1.2.1 スキップ判定アーキテクチャ
- **属性定義**: `[ExploratoryFact]` および `[ExploratoryTheory]` (`tests/GeminiGatewayClient.ExploratoryTests/Infrastructure/ExploratoryFactAttribute.cs`)
  - xUnit の `FactAttribute` / `TheoryAttribute` を継承。
  - 属性インスタンス化時に `ExploratoryTestSettings.IsConfigured` を評価。
  - API キーが解決できない場合、属性の `Skip` プロパティに「`Skipped because environment variable 'GeminiAPIKey' is not set. Set 'GeminiAPIKey' to run live exploratory tests.`」を設定。
  - xUnit のテストディスカバリ機構により、テストメソッド本体を実行することなく安全に `Skipped` としてマークされます。

```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExploratoryFactAttribute : FactAttribute
{
    private const string SkipMessage =
        "Skipped because environment variable 'GeminiAPIKey' is not set. Set 'GeminiAPIKey' to run live exploratory tests.";

    public ExploratoryFactAttribute()
    {
        if (!ExploratoryTestSettings.IsConfigured)
        {
            Skip = SkipMessage;
        }
    }
}
```

#### 1.2.2 環境変数の解決優先順位
`ExploratoryTestSettings.cs` は以下の優先順位で API キーおよびモデル設定を解決します：

| 項目 | 優先キー（Primary） | フォールバックキー（Fallback） | 参照スコープ |
|---|---|---|---|
| **API キー** | `GeminiAPIKey` | `GEMINI_API_KEY` | 1. `EnvironmentVariableTarget.Process`<br>2. `EnvironmentVariableTarget.User` (Windows のみ) |
| **テスト対象モデル** | `GeminiTestModel` | `GEMINI_TEST_MODEL` | 1. `EnvironmentVariableTarget.Process`<br>2. `EnvironmentVariableTarget.User`<br>3. デフォルト値 (`gemini-3-flash-preview`) |

Windows ユーザー環境変数をフォールバック先としてサポートしているため、ターミナルや IDE 再起動後も API キーを安全に保持しつつ、Git コミット等のリポジトリ資産には一切クレデンシャルを含めない運用が可能です。

#### 1.2.3 直列実行（Non-Parallelization）の強制
Gemini API の無料枠・プレビューモデルには 1 分間あたりのリクエスト数（RPM）およびトークン数（TPM）のレートリミットが存在します。探索的テストアセンブリ全体で直列実行を強制しています：
- アセンブリレベル: `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
- コレクションレベル: `[CollectionDefinition(CollectionName, DisableParallelization = true)]`
これにより、テスト間の並行実行による HTTP 429 (`RESOURCE_EXHAUSTED`) の発生を完全に防止しています。

---

### 1.3 インメモリ資産生成による環境非依存性

探索的テストにおいて画像、音声、PDF などのマルチモーダルデータを検証する際、リポジトリ内のローカルディスクファイル（例: `test.png`, `audio.wav`, `sample.pdf`）に依存することは**厳禁**とします。

#### ファイル依存排除の理由
1. **実行パスと作業ディレクトリの不整合**: IDE、`dotnet test` CLI、コンテナ、サブエージェント環境などで実行カレントディレクトリが異なり、ファイルパス解決エラーが発生しやすい。
2. **リポジトリの肥大化とバイナリ管理の複雑化**: バイナリファイルを Git 管理下に置くことによる差分管理や LFS 依存の回避。
3. **完全なポータビリティ**: 任意の OS（Windows, Linux, macOS）や一時的な CI コンテナ上で、ソースコード単体で完結して即座に実行可能。

#### 動的インメモリバイナリ生成仕様
すべてのマルチモーダル入力データは、C# の標準 API（`MemoryStream`, `BinaryPrimitives`, `Span<byte>`）を用いてテスト実行時にメモリ上でプロシージャルに合成します。

- **画像データ (PNG)**:
  RFC 2083 に準拠した最小構成の PNG バイト列を合成。
  - シグネチャ (`89 50 4E 47 0D 0A 1A 0A`)
  - `IHDR` チャンク（幅、高さ、8-bit Truecolor RGB）
  - `IDAT` チャンク（先頭フィルタバイト `0x00` + RGB バイト列を `System.IO.Compression.ZLibStream` で Deflate 圧縮）
  - `IEND` チャンク
- **音声データ (WAV)**:
  RIFF/WAVE フォーマットに準拠した 44 バイトの標準ヘッダー + 16-bit リニア PCM モノラル音声バイト列（16kHz または 44.1kHz）を生成。無音（全ゼロ）または明瞭な正弦波（440Hz 基準音）を数学的に算出し、モデルが音声として認識できる有効な波形データを提供。
- **文書データ (PDF)**:
  PDF 1.4 仕様に準拠したテキストレンダリング可能な最小構造のバイナリを動的生成。
  - ヘッダー: `%PDF-1.4`
  - オブジェクト 1: `/Type /Catalog /Pages 2 0 R`
  - オブジェクト 2: `/Type /Pages /Kids [3 0 R] /Count 1`
  - オブジェクト 3: `/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >>`
  - オブジェクト 4: コンテンツストリーム（`BT /F1 14 Tf 50 700 Td (検証用テキスト...) Tj ET`）
  - オブジェクト 5: 標準 Type 1 フォント `/Type /Font /Subtype /Type1 /BaseFont /Helvetica`
  - 相互参照テーブル (`xref`) および `trailer`、`startxref`、`%%EOF`
- **テキスト / CSV / コード**:
  `Encoding.UTF8.GetBytes(...)` によりインメモリで生成し、`Part.FromBytes` または `DataContent` に渡す。

---

## 2. Microsoft.Extensions.AI 連携のテスト観点体系

`GeminiGatewayClient` は `GeminiChatClient` (`IChatClient` の実装) を通じて `Microsoft.Extensions.AI` と統合されます。特に `Microsoft.Extensions.AI` が提供する標準デコレータ `FunctionInvokingChatClient` との連携は、実務におけるエージェント構築の中核となります。

```
[ Application Code ]
        │
        ▼
[ FunctionInvokingChatClient ]  (Microsoft.Extensions.AI)
        │
        ▼
[ Custom Middleware / Pipeline ] (ChatClientBuilder)
        │
        ▼
[ GeminiChatClient ]            (GeminiGatewayClient: IChatClient)
        │
        ▼
[ GeminiClient / HTTP ]         (Gemini REST API: models/gemini-3-flash-preview)
```

---

### 2.1 FunctionInvokingChatClient による自動ファンクション呼び出し

#### 2.1.1 呼び出しトポロジーの観点
1. **単一ツール呼び出し (Single Tool Invocation)**:
   ユーザーの質問に対して 1 回のツール呼び出しを行い、その結果を用いて回答を生成する基本パターン。
2. **順次チェーン呼び出し (Sequential Chaining)**:
   先行ツールの出力を後続ツールの入力として連鎖させるパターン（例: `顧客ID照会` → `割引率計算` → `注文確定`）。
   - 既存テスト `IChatClient_UseFunctionInvocation_AutomaticallyChainsFourLocalTools` で 4 ステップの自動連鎖が検証済み。
3. **並列ツール呼び出し (Parallel Tool Invocation)**:
   Gemini モデルが 1 回のアシスタントターンで複数のツール呼び出し（複数の `FunctionCallContent`）を同時に返すパターン。
   - **検証ポイント**:
     - 単一の質問（例:「東京と大阪の天気を同時に調べて」「フライトとホテルを同時に検索して」）に対して、Gemini が複数の `FunctionCall` を単一の `Candidate.Content.Parts` に含めて返却すること。
     - `GeminiChatClient` がそれらを個別の `FunctionCallContent` としてマッピングし、FinishReason を `ChatFinishReason.ToolCalls` に設定すること。
     - `FunctionInvokingChatClient` がこれら並列ツールを並行ディスパッチし、全ツールの結果を `ChatRole.Tool` のメッセージとして集約してモデルに差し戻すこと。
     - モデルがすべての結果を統合した最終回答を出力すること。

#### 2.1.2 引数なし関数 (Parameterless Functions) の仕様と検証
- **背景と課題**:
  Gemini REST API のスキーマバリデーションは非常に厳格であり、ツールの引数が存在しない場合に空の JSON スキーマオブジェクト `{ "type": "object", "properties": {} }` を送信すると、サーバーから `HTTP 400 Bad Request`（`"Invalid JSON payload received. Unknown property: properties"` 等）が返却されます。
- **クライアント実装仕様**:
  `ChatOptionsMapper.cs` の `ExtractParametersJsonSchema` では、引数なし（またはプロパティが空）の関数について `FunctionDeclaration.ParametersJsonSchema` を明示的に `null` に設定します。
- **検証ポイント**:
  - `AIFunctionFactory.Create(() => GetServerStatus(), "get_server_status")` のように、引数を一切持たない C# 関数を登録。
  - `FunctionInvokingChatClient` 経由で実行した際、HTTP 400 エラーが発生せず正常にツールが呼び出され、返却値が回答に反映されること。

#### 2.1.3 複雑な型・列挙型・コレクションのスキーマ生成とバインディング
実アプリケーションでは、ツールは単純な文字列や数値だけでなく、構造化されたオブジェクトやコレクションを受け取ります。
- **検証対象の型バリエーション**:
  1. **C# `enum`**: `enum PriorityLevel { Low, Normal, High, Urgent }`。JSON スキーマ側で `string` かつ `enum: ["Low", "Normal", "High", "Urgent"]` として展開され、モデルが適切な文字列を選択し、C# 側の引数に正しくアンボックスされること。
  2. **コレクション型**: `List<string> productCodes` や `int[] quantities`。JSON スキーマで `type: "array", items: { type: "string" }` と定義され、モデルが配列 JSON を生成して正しくバインドされること。
  3. **ネストされた複合型**: `DateRange { DateTime StartDate, DateTime EndDate }` や `CustomerFilter` クラス。
  4. **デフォルト値付きオプション引数**: `int limit = 10`。
- **戻り値のオブジェクトラップ仕様**:
  Gemini API は、ツールのレスポンス電文 (`FunctionResponse.response`) が必ず **JSON Object** であることを要求します。スカラー値（`int`, `string`, `bool`）や配列（`string[]`）を直接返すと API エラーとなります。
  `GeminiGatewayClient` の `ToolJsonConverter.ToResponseObject` は、非オブジェクトの戻り値を自動的に `{"result": value}` という形式のオブジェクトにラップして送信します。この透過的ラップが実 API 側で問題なく解釈されることを検証します。

#### 2.1.4 エラーハンドリングとモデルリカバリ (Error Handling & Recovery)
外部 API やデータベース障害により、ツール内部でエラーが発生する場合があります。
- **検証シナリオ**:
  - 存在しないエンティティの照会ツール（例: `GetUserAccount("INVALID_ID")`）が、エラー情報オブジェクト `{"error": "AccountNotFound", "message": "ID INVALID_ID does not exist", "suggestedAction": "Please verify user ID"}` を返却する。
  - `FunctionInvokingChatClient` はそのエラー情報を Gemini に戻す。
  - **期待されるモデルの振る舞い**:
    モデルがクラッシュしたり例外を投げたりせず、ツールから返されたエラー内容を正しく理解し、ユーザーに対して「アカウントが見つかりませんでした。別の ID を入力してください」といった親切な代替説明を回答として提示できること。

---

### 2.2 ストリーミング (`GetStreamingResponseAsync`) でのツール呼び出しとトークン集計

ストリーミングとファンクションコールを併用する場合、以下の重要なライフサイクルが発生します：
1. **ツール呼び出しフェーズ**:
   モデルがツール呼び出しをストリーミング出力する際、引数の断片を含む `ChatResponseUpdate` が発行される。
2. **自動実行とループバック**:
   `FunctionInvokingChatClient` がツールを実行し、その結果を持って再度モデルにリクエストを発行する。
3. **最終回答ストリーミング**:
   モデルがツール結果を踏まえたテキスト回答をトークン単位でストリーミング出力する。
4. **トークン二重計上の防止仕様**:
   `GeminiChatClient.cs` の `StreamCoreAsync` では、ストリーミング中の各チャンクで中間トークンを重複加算するのを防ぐため、ストリームの**末尾の単一更新**にのみ累積の `UsageContent` を付与して出力します。
- **検証ポイント**:
  - `IChatClient.GetStreamingResponseAsync` を `UseFunctionInvocation()` と併用した際、最終テキストがリアルタイムに逐次受信できること。
  - 全ストリーム終了後に得られる `ChatResponse` において、`Usage.TotalTokenCount` や `Usage.InputTokenCount` が二重計上されず、適切な値となっていること。
  - 内部思考モデル（Thinking モデル）を併用した場合に、ストリーミングチャンク間で `ThoughtSignature` が失われず保持されること。

---

### 2.3 ChatClientBuilder によるパイプライン構築

`ChatClientBuilder` は ASP.NET Core のミドルウェアと同様に、複数の `DelegatingChatClient` をスタックして強力な処理パイプラインを構成します。

```csharp
IChatClient client = new ChatClientBuilder(geminiChatClient)
    .Use(customAuditingMiddleware)    // 監査・呼び出しロギング
    .UseFunctionInvocation()          // 自動ファンクション呼び出し
    .Build();
```

- **検証観点**:
  1. **多段ミドルウェアの連鎖実行**:
     リクエストとレスポンスをインターセプトするカスタムデリゲーティングクライアントを挿入し、ツール呼び出しの各ラウンド（ユーザー発話時、ツール実行結果送信時、最終回答受信時）で適切にフックが実行され、内部カウントやログが記録されること。
  2. **`GetService<T>` の透過性 (Service Provider Transparency)**:
     最外層のラッパーから `client.GetService<GeminiChatClient>()` や `client.GetService<IGeminiClient>()`、`client.GetService<ChatClientMetadata>()` を呼び出した際に、パイプラインを透過して下位のターゲットインスタンスが正しく取得できること。

---

### 2.4 構造化出力・マルチモーダル・ReasoningOptions（Thinking）とのツール併用

1. **構造化出力 (`ChatResponseFormat.ForJsonSchema`)**:
   `IChatClient` レイヤーから `ChatOptions.ResponseFormat` に JSON スキーマを指定した場合、`ChatOptionsMapper` が `GenerationConfig.ResponseJsonSchema` にマッピングし、モデルが指定した JSON スキーマ通りの出力を返すこと。
2. **マルチモーダル入力 (`DataContent`)**:
   `ChatMessage` にテキストだけでなく `DataContent`（インライン画像や PDF、音声のバイト列）を含めて `IChatClient.GetResponseAsync` を呼び出した際、`ChatRequestMapper` が適切に `Part.FromBytes` に変換して実 API が内容を解析できること。
3. **ReasoningOptions (Thinking) とファンクションコールの併用**:
   - `gemini-3-flash-preview` 等の推論モデルで `ChatOptions.ReasoningOptions`（`ReasoningEffort.Low` 等）を指定し、かつ `UseFunctionInvocation()` を有効化。
   - モデルがツール呼び出し前に推論（Thinking）を行い、ツール結果受信後にも推論を行って最終回答を導出するフローを検証。
   - **Thought Signature の保持**:
     Gemini は推論過程の改ざんを防止するため、推論を含むメッセージに `thoughtSignature` を付与します。`FunctionInvokingChatClient` が複数ターンを往復する際、この署名が維持され、Google サーバーから「署名が無効または欠落している」というエラー（HTTP 400）が発生しないことを実証。
4. **ToolMode の強制制御**:
   - `ChatToolMode.Auto`: 通常の自動判断。
   - `ChatToolMode.None`: ツールが登録されていても一切のツール呼び出しを禁止し、テキスト回答を強制する。
   - `ChatToolMode.RequireAny`: いずれかのツールを必ず呼び出すよう強制する。

---

## 3. GeminiGatewayClient ネイティブ機能のテスト観点体系

`GeminiGatewayClient` の核となる `IGeminiClient` / `GeminiClient` は、Google Gemini REST API のフルスペックを直接操作する低レベルかつ高機能なインターフェースです。

---

### 3.1 マルチモーダルと MediaResolution 制御

Gemini API は、画像だけでなく音声、PDF ドキュメント、ソースコードなどの多様なメディア形式をネイティブにサポートしています。

```
[ In-Memory Byte Synthesis ]
  ├── PNG (RFC 2083)  ──► Part.FromBytes(data, MimeTypes.ImagePng)
  ├── WAV (PCM 16bit) ──► Part.FromBytes(data, MimeTypes.AudioWav)
  ├── PDF (PDF-1.4)   ──► Part.FromBytes(data, MimeTypes.Pdf)
  └── Code / Text     ──► Part.FromBytes(data, MimeTypes.TextPython)
                                │
                                ▼
                   [ GenerateContentRequest ]
                     ├── Contents: [ Content(Role.User, parts) ]
                     └── GenerationConfig: MediaResolution (Low / Medium / High)
```

#### 3.1.1 メディア種別ごとの検証観点
1. **音声入力 (`audio/wav`)**:
   - 16-bit リニア PCM WAV データをインメモリで生成し、`Part.FromBytes(..., MimeTypes.AudioWav)` として送信。
   - モデルが音声を理解し、内容に関する質問（例:「この音声には何の音が録音されていますか？」）に対して応答できること。
2. **文書入力 (`application/pdf`)**:
   - 表題や特定のキーワード、文章が描画された最小構造の PDF バイナリをインメモリで生成し、`Part.FromBytes(..., MimeTypes.Pdf)` として送信。
   - モデルが PDF 内のテキスト構造を正しく抽出し、質問に対する的確な回答を行えること。
3. **テキスト/コードファイル入力 (`text/plain`, `text/x-python`, `text/csv`)**:
   - CSV データや Python スクリプトをバイナリ Part として直接提供し、モデルが構文解析・分析できること。

#### 3.1.2 MediaResolution 制御とトークン消費量の検証
- `GenerationConfig.MediaResolution` 設定値:
  - `MediaResolution.Low`: 1 画像/フレームあたり約 64 トークン。低解像度でトークンを大幅に節約。
  - `MediaResolution.Medium`: 1 画像/フレームあたり約 256 トークン。標準解像度。
  - `MediaResolution.High`: 高解像度ズーム再サンプリング（最大 256 トークン×領域）。
- **検証ポイント**:
  同一のインメモリ画像に対して `MediaResolution.Low` と `MediaResolution.Medium` をそれぞれ指定して `GenerateContentAsync` を呼び出し、レスポンスの `UsageMetadata.PromptTokenCount` を比較。
  `Low` の方が明らかに少ないプロンプトトークン数を消費していることを実測値で検証。

---

### 3.2 サーバーサイド Python コード実行 (`Tool.CodeExecution`)

Gemini は、モデル自身が Python コードを記述し、Google が管理するセキュアなサンドボックス環境で実行してその計算結果を回答に利用する「Code Execution」ツールをネイティブで提供しています。

#### 3.2.1 リクエストとレスポンスの電文構造
- **リクエスト設定**:
  ```csharp
  var request = new GenerateContentRequest
  {
      Contents = [Content.FromText("第30番目のフィボナッチ数を計算し、その素因数分解を行ってください。")],
      Tools = [new Tool { CodeExecution = new CodeExecutionTool() }]
  };
  ```
- **レスポンス Part の構成**:
  モデルがコード実行を行った場合、返却される `Candidate.Content.Parts` には以下の順序で特別な Part が含まれます：
  1. `Part.ExecutableCode`:
     - `Language`: `CodeExecutionLanguage.Python`
     - `Code`: モデルが生成した Python スクリプト（例: `def fib(n): ...`）
  2. `Part.CodeExecutionResult`:
     - `Outcome`: `CodeExecutionOutcome.Ok`（または `Failed`, `DeadlineExceeded`）
     - `Output`: サンドボックス内の `stdout` / `stderr` 出力テキスト
  3. `Part.Text`:
     - コード実行結果を踏まえた、ユーザー向けの最終的な自然言語回答。

#### 3.2.2 検証ポイント
- 複雑な数学的計算やアルゴリズム問題をプロンプトとして送信。
- レスポンスの `Candidate.Content.Parts` 内に `ExecutableCode` および `CodeExecutionResult` が含まれていることを検証。
- `ExecutableCode.Code` に有効な Python コードが含まれ、`CodeExecutionResult.Outcome == CodeExecutionOutcome.Ok` であることを確認。
- 最終テキストに計算結果の正解が含まれていること。

---

### 3.3 ネイティブ OpenAPI 3.03 Schema 構造化出力

`GeminiGatewayClient` には、Google Gemini の OpenAPI 3.03 サブセットスキーマを表現する強力なネイティブ型定義群（`Models/Schema.cs`, `Models/SchemaType.cs`）が実装されています。

#### 3.3.1 スキーマ定義の検証観点
- `GenerationConfig.ResponseSchema` に以下のネイティブオブジェクトを直接構築：
  ```csharp
  config.ResponseMimeType = MimeTypes.ApplicationJson;
  config.ResponseSchema = new Schema
  {
      Type = SchemaType.Object,
      Description = "製品カタログ情報",
      Properties = new Dictionary<string, Schema>
      {
          ["productId"] = new Schema { Type = SchemaType.String, Format = "uuid" },
          ["productName"] = new Schema { Type = SchemaType.String },
          ["price"] = new Schema { Type = SchemaType.Integer, Minimum = 0 },
          ["category"] = new Schema
          {
              Type = SchemaType.String,
              Enum = ["Electronics", "Books", "Apparel", "Home"]
          },
          ["tags"] = new Schema
          {
              Type = SchemaType.Array,
              Items = new Schema { Type = SchemaType.String }
          }
      },
      Required = ["productId", "productName", "price", "category"],
      PropertyOrdering = ["productId", "productName", "price", "category", "tags"]
  };
  ```
- **検証ポイント**:
  - 実 API がこのネイティブスキーマ定義を正常に受理し、HTTP 400 を返さないこと。
  - レスポンステキストが厳格にこのスキーマに適合する JSON 文字列として生成されること。
  - C# の厳格なモデルクラスへ `JsonSerializer.Deserialize<T>` で例外なくマッピングできること。
- **相互排他性バリデーション**:
  `ResponseSchema` と `ResponseJsonSchema` (`JsonElement`) を同時に設定した場合、リクエスト送信前の `GenerationConfig.Validate()` で `ArgumentException` がスローされること。

---

### 3.4 コンテキストキャッシュ (`CachedContent`)

Gemini API では、大量のプロンプト（ドキュメント集、コードベース、長いシステム指示）をサーバー側に事前キャッシュし、以降のリクエストで `cachedContent` リソース名を参照することで、レイテンシと課金トークン数を削減する機能が提供されています。

#### 3.4.1 スコープ境界とクライアントの役割
`docs/API_COMPATIBILITY.md` に明記されている通り、ステートレスクライアントである `GeminiGatewayClient` は**キャッシュの管理エンドポイント（作成・更新・削除・一覧）を意図的にサポート外**としています。しかし、生成リクエストにおける**キャッシュの消費（参照）**はファーストクラスでサポートしています。

- **リクエスト**: `GenerateContentRequest.CachedContent = "cachedContents/sample-cache-id"`
- **レスポンス**: `UsageMetadata.CachedContentTokenCount`（キャッシュから読み出されたプロンプトトークン数）
- **MEAI 連携**: `ChatOptions.AdditionalProperties["cachedContent"]` からのリクエストマッピングおよび `Usage.CachedInputTokenCount` への透過的マッピング。

#### 3.4.2 探索的テストでの検証アプローチ
キャッシュ作成は最小 32,768 トークン以上のプロンプトと時間課金が必要となるため、CI 上で一時キャッシュを都度作成することは現実的ではありません。
したがって、探索的テストでは以下の 2 点を検証します：
1. `CachedContent` プロパティを設定した際、電文が正しく JSON シリアライズされ、Google API サーバーへ伝播すること（実キー環境で存在しないキャッシュ ID を指定した場合、サーバーから「Cache not found (HTTP 404)」が正当に返ることを確認することで、電文到達性を実証）。
2. キャッシュが有効なレスポンスを受信した際、`UsageMetadata.CachedContentTokenCount` が正しくパースされ、MEAI の `CachedInputTokenCount` に値が反映されること。

---

### 3.5 セーフティ設定 (`SafetySetting` / `SafetyRatings`)

Gemini は、ヘイトスピーチ、ハラスメント、危険なコンテンツなどの有害コンテンツをフィルタリングするセーフティ機構を備えています。

- **リクエスト設定**:
  `GenerateContentRequest.SafetySettings` に `SafetySetting` のリストを指定：
  - `Category`: `HarmCategory.Harassment`, `HarmCategory.HateSpeech`, `HarmCategory.DangerousContent`, `HarmCategory.SexuallyExplicit`
  - `Threshold`: `HarmBlockThreshold.BlockOnlyHigh`, `HarmBlockThreshold.BlockMediumAndAbove`, `HarmBlockThreshold.BlockLowAndAbove`, `HarmBlockThreshold.BlockNone`
- **レスポンスフィードバック**:
  - `Candidate.SafetyRatings`: 各カテゴリにおける確率（`HarmProbability`）と深刻度（`HarmSeverity`）。
  - `PromptFeedback.BlockReason`: プロンプト全体が有害と判定された場合のブロック理由。
  - `response.IsBlocked`: `FinishReason.Safety` または `PromptFeedback.BlockReason` に基づくブロック判定ヘルパー。
- **検証ポイント**:
  カスタムのセーフティ閾値を設定したリクエストが実 API で正しく受け入れられ、無害なプロンプトに対して各カテゴリの `SafetyRating` リスト（評価スコア）がレスポンスに正しく返却されること。

---

### 3.6 ツール制御とグラウンディング

#### 3.6.1 FunctionCallingMode による強制制御
`ToolConfig.FunctionCallingConfig` は、モデルのツール呼び出し動作を強制制御します：
- `FunctionCallingMode.Auto`: モデルが必要に応じてツールの呼び出しを判断（デフォルト）。
- `FunctionCallingMode.Any`: 登録されたツールのうち、いずれかのツール呼び出しを強制。
- `FunctionCallingMode.None`: **ツール定義が存在する場合であっても、ツール呼び出しを厳格に禁止し、通常の自然言語テキスト回答を強制する**。
- **検証ポイント (`FunctionCallingMode.None`)**:
  ツール定義を多数含めたリクエストに対して `FunctionCallingMode.None` を指定。質問が「ツールを使わなければ答えられない」ような内容であっても、モデルは `FunctionCall` を 1 つも生成せず、自然言語で「ツールを呼び出すことができません」または推測のテキストを返すことを検証。

#### 3.6.2 グラウンディング統合
- `Tool.GoogleSearch` (`GoogleSearchTool`):
  最新の出来事に関する質問に対し、Google 検索を実行。`Candidate.GroundingMetadata` に検索クエリ（`WebSearchQueries`）や参照チャンク（`GroundingChunks`）が返却されること。
- `Tool.UrlContext` (`UrlContextTool`):
  指定された URL の内容を取得してグラウンディング情報を付与すること。

---

## 4. 既存テストの網羅状況と差分分析（マトリクス表形式）

現行の `tests/GeminiGatewayClient.ExploratoryTests`（全 24 件）および `tests/GeminiGatewayClient.Tests`（全 185 件）の網羅状況を調査し、実 API に対する探索的テストのギャップをマトリクス形式で整理しました。

### 凡例
- **Covered (網羅)**: 実 API に対する探索的テストが存在し、動作が確認されている。
- **Partial (一部のみ)**: 単体テストまたは一部のユースケースのみ存在し、実 API 連携の重要な側面が未検証。
- **Missing (不足)**: 実 API に対する探索的テストが一切存在しない。

### 網羅状況マトリクス表

| No | 大分類 | 機能・検証観点 | API サーフェス | 単体テスト状況 | 探索的テスト現況 | ギャップ評価 | ギャップ ID |
|:--:|:---|:---|:---:|:---:|:---:|:---:|:---:|
| 1 | MEAI | 順次ファンクションコール (`UseFunctionInvocation`) | `IChatClient` | ✅ 網羅 | ✅ 4段階順次ツール検証済 (`ChatClientIntegrationExploratoryTests`) | Covered | - |
| 2 | MEAI | ストリーミングファンクションコール | `IChatClient` | ✅ 網羅 | ✅ 2段階順次ストリーミング検証済 | Covered | - |
| 3 | MEAI | **並列ファンクションコール自動実行** | `IChatClient` | ✅ 網羅 | ❌ ネイティブテストのみで MEAI 未検証 | **Missing** | **GAP-MEAI-01** |
| 4 | MEAI | **複合型・列挙型・配列のツール引数バインディング** | `IChatClient` | ✅ 網羅 | ❌ プリミティブ（string/int）のみ検証 | **Missing** | **GAP-MEAI-02** |
| 5 | MEAI | **ツール内部エラーとモデルのリカバリ** | `IChatClient` | ❌ 未作成 | ❌ 実 API 未検証 | **Missing** | **GAP-MEAI-03** |
| 6 | MEAI | **ChatClientBuilder 多段ミドルウェア構築** | `IChatClient` | ❌ 未作成 | ❌ 単一 `UseFunctionInvocation` のみ | **Missing** | **GAP-MEAI-04** |
| 7 | MEAI | **引数なし関数の自動呼び出し (空スキーマ回避)** | `IChatClient` | ✅ 網羅 | ❌ 実 API 未検証 | **Missing** | **GAP-MEAI-05** |
| 8 | MEAI | **Thinking (ReasoningOptions) とツールの併用** | `IChatClient` | ✅ 網羅 | ❌ ツールなしの推論のみ検証 | **Missing** | **GAP-MEAI-06** |
| 9 | MEAI | 構造化出力 (`ChatResponseFormat.ForJsonSchema`) | `IChatClient` | ✅ 網羅 | ❌ ネイティブのみで MEAI 未検証 | **Missing** | **GAP-MEAI-07** |
| 10 | MEAI | マルチモーダル入力 (`DataContent`) | `IChatClient` | ✅ 網羅 | ❌ ネイティブのみで MEAI 未検証 | **Missing** | **GAP-MEAI-08** |
| 11 | Native | テキスト生成 & 基本パラメータ | `IGeminiClient` | ✅ 網羅 | ✅ 検証済 (`BasicGenerationExploratoryTests`) | Covered | - |
| 12 | Native | SSE / JsonArray ストリーミング | `IGeminiClient` | ✅ 網羅 | ✅ 両フォーマット検証済 | Covered | - |
| 13 | Native | ThinkingConfig (推論制御) | `IGeminiClient` | ✅ 網羅 | ✅ トークン消費・トランケーション検証済 | Covered | - |
| 14 | Native | インライン画像マルチモーダル (PNG) | `IGeminiClient` | ✅ 網羅 | ✅ 32x32 PNG 検証済 | Covered | - |
| 15 | Native | **音声マルチモーダル (PCM WAV)** | `IGeminiClient` | ✅ 網羅 | ❌ 音声実 API 未検証 | **Missing** | **GAP-NAT-01** |
| 16 | Native | **PDF ドキュメントマルチモーダル** | `IGeminiClient` | ✅ 網羅 | ❌ PDF 実 API 未検証 | **Missing** | **GAP-NAT-02** |
| 17 | Native | **MediaResolution (Low vs Medium) とトークン** | `IGeminiClient` | ✅ 網羅 | ❌ トークン消費差の実測未検証 | **Missing** | **GAP-NAT-03** |
| 18 | Native | **サーバーサイド Python コード実行 (`Tool.CodeExecution`)** | `IGeminiClient` | ✅ 網羅 | ❌ コード実行サンドボックス未検証 | **Missing** | **GAP-NAT-04** |
| 19 | Native | **ネイティブ OpenAPI 3.03 Schema 構造化出力** | `IGeminiClient` | ✅ 網羅 | ❌ JsonElement のみで Schema 未検証 | **Missing** | **GAP-NAT-05** |
| 20 | Native | **セーフティ設定 (`SafetySetting`) と SafetyRatings** | `IGeminiClient` | ✅ 網羅 | ❌ 実 API での評価値未検証 | **Missing** | **GAP-NAT-06** |
| 21 | Native | **ツール呼び出し強制抑制 (`FunctionCallingMode.None`)** | `IGeminiClient` | ✅ 網羅 | ❌ Mode=Any のみで None 未検証 | **Missing** | **GAP-NAT-07** |
| 22 | Native | **コンテキストキャッシュ電文伝播 (`CachedContent`)** | `IGeminiClient` | ✅ 網羅 | ❌ 実 API 未検証 | **Missing** | **GAP-NAT-08** |
| 23 | Native | Google Search Grounding | `IGeminiClient` | ✅ 網羅 | ✅ GroundingMetadata 検証済 | Covered | - |
| 24 | Native | URL Context Grounding | `IGeminiClient` | ✅ 網羅 | ✅ UrlContextMetadata 検証済 | Covered | - |
| 25 | Native | Embeddings 単一 & バッチ生成 | `IGeminiClient` | ✅ 網羅 | ✅ コサイン類似度検証済 | Covered | - |
| 26 | Native | 長期対話コンテキスト保持 (6ターン) | `IGeminiClient` | ✅ 網羅 | ✅ トークン単調増加検証済 | Covered | - |

---

## 5. 不足しているテスト観点の詳細と追加対象テストケース一覧

Milestone 2 において `tests/GeminiGatewayClient.ExploratoryTests` に実装すべき具体的な追加テストケースの仕様を以下に定義します。

すべてのテストケースは `[ExploratoryFact]` を使用し、`GeminiAPIKey` 未設定時はクリーンにスキップされ、設定時は実 API と通信して動作確認を行います。

---

### 5.1 MEAI 連携の新規テストケース一覧 (`ChatClientIntegrationExploratoryTests.cs`)

#### TC-MEAI-01: 並列ツール呼び出し自動ディスパッチの検証
- **テストメソッド名**: `IChatClient_UseFunctionInvocation_ParallelToolCalls_DispatchesConcurrentlyAndSynthesizesAnswer`
- **対象機能**: `FunctionInvokingChatClient` の並列ファンクションコール処理能力 (GAP-MEAI-01)
- **前提条件・ツールセット**:
  - `get_branch_weather(string branchCity)`: 支店名から現地の天気を返す。
  - `get_branch_stock(string branchCity, string itemSku)`: 支店名と商品コードから在庫数を返す。
- **入力プロンプト**:
  「札幌支店 (`Sapporo`) と福岡支店 (`Fukuoka`) の両方について、現地の天気と商品 `SKU-DRIVE-2TB` の在庫数を**同時に**調べて比較報告してください。」
- **期待される振る舞い・アサーション**:
  1. Gemini が単一ターンで 2 つ以上のツール呼び出し（計 4 呼び出し、または 2 支店並列）を発行すること。
  2. `FunctionInvokingChatClient` がそれらすべてを漏れなく実行し、実行トレースリストに全呼び出しが記録されていること。
  3. 最終レスポンステキストに両支店の天気および在庫数の両方の情報が含まれていること。

#### TC-MEAI-02: 複合型・列挙型・配列パラメータのスキーマ生成とバインディング検証
- **テストメソッド名**: `IChatClient_UseFunctionInvocation_ComplexParametersAndEnums_BindsArgumentsCorrectly`
- **対象機能**: `AIJsonUtilities.CreateJsonSchema` と `ToolJsonConverter` による高度な型バインディング (GAP-MEAI-02)
- **前提条件・型定義**:
  - `enum SupportPriority { Low, Normal, High, Urgent }`
  - `record TicketFilter(string Department, SupportPriority Priority, List<string> Tags, int MaxCount = 5)`
  - ツール関数: `query_support_tickets(TicketFilter filter)`
- **入力プロンプト**:
  「ITサポート部門 (`ITSupport`) のチケットで、優先度が `Urgent`、タグに `Network` と `Security` が付いているものを最大 3 件検索してください。」
- **期待される振る舞い・アサーション**:
  1. Gemini が C# 側の `TicketFilter` クラス定義に従った JSON オブジェクト引数を生成すること。
  2. `SupportPriority.Urgent` が列挙型として正しくバインドされ、`Tags` リストに `"Network"` と `"Security"` が含まれ、`MaxCount` が 3 に設定されていること。
  3. ツールが返した検索結果が最終回答に反映されること。

#### TC-MEAI-03: ツールエラー発生時のモデル自律リカバリ検証
- **テストメソッド名**: `IChatClient_UseFunctionInvocation_ToolErrorPayload_ModelRecoversGracefully`
- **対象機能**: ツール例外・エラーペイロードに対する `FunctionInvokingChatClient` とモデルの回復力 (GAP-MEAI-03)
- **前提条件・ツールセット**:
  - `find_user_profile(string userId)`: `userId == "USER-999"` の場合に `new { status = "ERROR", errorCode = "USER_NOT_FOUND", message = "User USER-999 does not exist in directory" }` を返す。
  - `find_fallback_contact(string department)`: 代替の部署連絡先を返す。
- **入力プロンプト**:
  「ユーザー `USER-999` の連絡先を調べてください。もしユーザーが見つからなければ、人事部 (`HR`) の連絡先窓口を調べて案内してください。」
- **期待される振る舞い・アサーション**:
  1. 最初に対象ユーザーの検索ツールが実行され、エラー結果がモデルに返ること。
  2. モデルがエラーを理解し、例外で終了することなく、続いて代替ツール `find_fallback_contact` を呼び出すか、ユーザーが見つからなかった旨を明示して適切に回答すること。

#### TC-MEAI-04: ChatClientBuilder 多段ミドルウェアパイプラインの検証
- **テストメソッド名**: `IChatClient_ChatClientBuilder_CustomAuditingMiddleware_InterceptsAndTracksExecution`
- **対象機能**: `ChatClientBuilder` によるミドルウェアスタックと `GetService<T>` の透過性 (GAP-MEAI-04)
- **前提条件**:
  - カスタムミドルウェア `AuditingChatClient : DelegatingChatClient` を実装。各 `GetResponseAsync` 呼び出しのリクエスト/レスポンス回数とトークン数を集計。
  - パイプライン構成: `new ChatClientBuilder(geminiChatClient).Use(auditor).UseFunctionInvocation().Build()`
- **期待される振る舞い・アサーション**:
  1. ツール呼び出しを伴うリクエストにおいて、ミドルウェアが内部ターンを含む全往復を傍受し、カウントが 2 回以上記録されること。
  2. `pipeline.GetService<GeminiChatClient>()` が `null` でなく、基盤のクライアントインスタンスが取得できること。

#### TC-MEAI-05: 引数なし関数の自動呼び出し検証 (空スキーマの抑制)
- **テストメソッド名**: `IChatClient_UseFunctionInvocation_ParameterlessFunction_ExecutesWithoutError`
- **対象機能**: パラメータなし関数定義で `parametersJsonSchema == null` が適用され、HTTP 400 を回避できること (GAP-MEAI-05)
- **前提条件**:
  - `[Description("現在稼働中のゲートウェイサーバーのシステム稼働状況を取得します。")] object GetSystemStatus()`
- **入力プロンプト**:
  「現在のシステム稼働状況を確認して教えてください。」
- **期待される振る舞い・アサーション**:
  1. HTTP 400 Bad Request を発生させずに API 呼び出しが成功すること。
  2. `GetSystemStatus` が実行され、その結果（稼働率やステータス）が回答に含まれること。

#### TC-MEAI-06: 推論モデル (ReasoningOptions) とファンクションコールの併用検証
- **テストメソッド名**: `IChatClient_UseFunctionInvocation_WithReasoningOptions_PreservesThoughtSignaturesAcrossTurns`
- **対象機能**: 推論プロセスを伴うマルチターンツール呼び出しでの Thought Signature 整合性 (GAP-MEAI-06)
- **前提条件**:
  - `ChatOptions.ReasoningOptions = new() { Effort = ReasoningEffort.Low }`
  - ツール関数と `UseFunctionInvocation()` を設定。
- **入力プロンプト**:
  論理的推論と数値計算を要するプロンプト。
- **期待される振る舞い・アサーション**:
  1. 内部ターンをまたぐ際に `ThoughtSignature` が適切に引き継がれ、署名検証エラー（HTTP 400）が発生しないこと。
  2. 最終応答に推論トークンまたは推論内容（`TextReasoningContent`）が確認でき、最終回答が得られること。

---

### 5.2 Native 機能の新規テストケース一覧 (`NativeCapabilitiesExploratoryTests.cs`)

#### TC-NAT-01: インライン PCM WAV 音声マルチモーダル認識の検証
- **テストメソッド名**: `Multimodal_InlineWavAudio_RecognizesAudioData`
- **対象機能**: `Part.FromBytes` による WAV 音声入力の理解 (GAP-NAT-01)
- **インメモリ資産生成**:
  44 バイトの RIFF/WAVE ヘッダー + 16kHz モノラル PCM データ（1 秒間の 440Hz 基準音 または 無音波形）を合成。
- **リクエスト構成**:
  `GenerateContentRequest` にテキスト「提供された音声ファイルの内容・長さを説明してください。」と合成 WAV バイト列 (`MimeTypes.AudioWav`) を添付。
- **期待される振る舞い・アサーション**:
  1. API がエラーなく応答を返すこと。
  2. モデルが音声ファイルであることを認識し、音声に関する説明を出力すること。

#### TC-NAT-02: インライン PDF ドキュメント構造化抽出の検証
- **テストメソッド名**: `Multimodal_InlinePdfDocument_ExtractsDocumentTextAndAnswersQuestion`
- **対象機能**: `Part.FromBytes` による PDF ドキュメント解析 (GAP-NAT-02)
- **インメモリ資産生成**:
  「Gemini Gateway Client Specification v1.0, Confidential Code: ALPHA-7729」というテキストが描画された最小構造の PDF 1.4 バイナリをインメモリで合成。
- **リクエスト構成**:
  PDF バイナリ (`MimeTypes.Pdf`) と質問「PDF 内に記載されている Confidential Code の値を抽出してください。」を送信。
- **期待される振る舞い・アサーション**:
  1. モデルの回答に `"ALPHA-7729"` が明確に含まれていること。

#### TC-NAT-03: MediaResolution (Low vs Medium) とトークン消費量の比較検証
- **テストメソッド名**: `Multimodal_MediaResolution_LowVsMedium_ReflectsTokenConsumptionDifference`
- **対象機能**: `GenerationConfig.MediaResolution` のトークン削減効果 (GAP-NAT-03)
- **インメモリ資産生成**: 64x64 のインメモリ PNG 画像。
- **テスト手順**:
  1. `MediaResolution.Low` を設定して画像を送信し、`UsageMetadata.PromptTokenCount` ($T_{low}$) を取得。
  2. `MediaResolution.Medium` を設定して同一画像を送信し、`UsageMetadata.PromptTokenCount` ($T_{med}$) を取得。
- **期待される振る舞い・アサーション**:
  $T_{low} < T_{med}$（`Low` 解像度の方がプロンプトトークン数が少ないこと）が実測されること。

#### TC-NAT-04: サーバーサイド Python コード実行 (`Tool.CodeExecution`) の検証
- **テストメソッド名**: `CodeExecution_ServerSidePython_ExecutesCodeAndIntegratesResult`
- **対象機能**: `Tool.CodeExecution` と `Part.ExecutableCode` / `Part.CodeExecutionResult` のハンドリング (GAP-NAT-04)
- **リクエスト構成**:
  - `Tools = [new Tool { CodeExecution = new CodeExecutionTool() }]`
  - プロンプト:「Python コードを実行して、1 から 100 までの整数のうち 7 の倍数の合計を求めてください。」
- **期待される振る舞い・アサーション**:
  1. 返却された `Candidate.Content.Parts` に `ExecutableCode` が含まれ、`Language == CodeExecutionLanguage.Python` であること。
  2. `CodeExecutionResult` が含まれ、`Outcome == CodeExecutionOutcome.Ok` であること。
  3. 最終回答に計算結果の正解（735）が含まれていること。

#### TC-NAT-05: ネイティブ OpenAPI 3.03 Schema 構造化出力の検証
- **テストメソッド名**: `StructuredOutput_NativeOpenApi3Schema_EnforcesJsonStructure`
- **対象機能**: `GenerationConfig.ResponseSchema` と `SchemaType` による型安全な出力制約 (GAP-NAT-05)
- **スキーマ定義**:
  `SchemaType.Object` をルートとし、`companyName` (String), `foundedYear` (Integer), `isPublic` (Boolean), `headquarters` (String) を持つスキーマをネイティブ型で構築。
- **期待される振る舞い・アサーション**:
  1. レスポンスが完全な JSON 文字列として生成されること。
  2. 指定スキーマに対応する C# レコード型に `JsonSerializer.Deserialize` で直接デシリアライズできること。

#### TC-NAT-06: セーフティ設定 (`SafetySetting`) と SafetyRatings の検証
- **テストメソッド名**: `SafetySettings_CustomThresholds_PopulatesCandidateSafetyRatings`
- **対象機能**: `GenerateContentRequest.SafetySettings` の反映と `SafetyRating` の取得 (GAP-NAT-06)
- **リクエスト構成**:
  4 つの主要カテゴリ（ハラスメント、ヘイト、性的、危険）に対して `HarmBlockThreshold.BlockOnlyHigh` を明示的に設定して一般的な質問を送信。
- **期待される振る舞い・アサーション**:
  1. レスポンスの `Candidate.SafetyRatings` に各カテゴリの評価オブジェクトが複数含まれていること。
  2. 各評価の `Category` が設定したカテゴリと一致し、`Probability` が設定されていること。
  3. 正常な入力に対して `response.IsBlocked` が `false` であること。

#### TC-NAT-07: ツール呼び出しの強制抑制 (`FunctionCallingMode.None`) の検証
- **テストメソッド名**: `ToolConfig_FunctionCallingModeNone_SuppressesToolInvocation`
- **対象機能**: `FunctionCallingMode.None` による自然言語出力の強制 (GAP-NAT-07)
- **リクエスト構成**:
  - 天気取得ツール `get_weather` を `GenerateContentRequest.Tools` に登録。
  - `ToolConfig = new() { FunctionCallingConfig = new() { Mode = FunctionCallingMode.None } }` を指定。
  - プロンプト:「東京の現在の天気を調べて教えてください。」
- **期待される振る舞い・アサーション**:
  1. 通常であればツールを呼ぶようなプロンプトであっても、返却される `Parts` 内に `FunctionCall` が **0 個** であること。
  2. モデルが自然言語テキストのみで応答すること。

#### TC-NAT-08: コンテキストキャッシュ電文伝播 (`CachedContent`) の検証
- **テストメソッド名**: `ContextCaching_CachedContentResource_PropagatesToWire`
- **対象機能**: `GenerateContentRequest.CachedContent` のシリアライズと電文伝播 (GAP-NAT-08)
- **リクエスト構成**:
  `request.CachedContent = "cachedContents/exploratory-nonexistent-cache-id"`
- **期待される振る舞い・アサーション**:
  1. クライアント側でパラメータが正しく電文に含まれて Google サーバーへ送信されること。
  2. 実 API から「キャッシュが見つからない」ことを示す `GeminiApiException`（HTTP 404 または HTTP 400）がスローされ、その詳細メッセージに指定したキャッシュリソース名またはキャッシュ関連のメッセージが含まれていること（電文がサーバーに確実に到達したことの証明）。

---

## 6. 実装・検証ガイドライン

### 6.1 テストコードの実装原則
1. **公開 API のみを使用**:
   `tests/GeminiGatewayClient.ExploratoryTests` は `[InternalsVisibleTo]` の対象外であり、一般の開発者が NuGet パッケージを利用するのと同一の公開 API サーフェスのみで実装する。
2. **ログ出力の充実**:
   各テストの開始時に `LogSection(title)` を呼び出し、レスポンス取得後に `LogChatResponseSummary(response)` または `LogResponseSummary(response)` を呼び出して、テストランナーの出力ログに電文のメタデータ、トークン数、ツール実行状況が明瞭に記録されるようにする。
3. **リソースの適切な破棄**:
   `GeminiClient` や `IChatClient` のインスタンスは `using` 宣言を用いて適切にスコープ内で破棄する。
4. **ビルドおよび単体テストの健全性維持**:
   探索的テストコードを追加・変更した際は、必ず以下の検証コマンドを実行して、警告・エラーのないクリーンな状態を維持する。

### 6.2 独立検証コマンド集

```pwsh
# 1. 探索的テストプロジェクトのコンパイル確認（警告・エラーゼロ）
dotnet build tests/GeminiGatewayClient.ExploratoryTests/GeminiGatewayClient.ExploratoryTests.csproj

# 2. 既存単体テストの全件成功確認（リグレッション防止、全 185 件パス）
dotnet test tests/GeminiGatewayClient.Tests/GeminiGatewayClient.Tests.csproj

# 3. 探索的テストの実行（GeminiAPIKey が設定されている場合は実API実行、未設定時は全件スキップ）
dotnet test tests/GeminiGatewayClient.ExploratoryTests/GeminiGatewayClient.ExploratoryTests.csproj
```
