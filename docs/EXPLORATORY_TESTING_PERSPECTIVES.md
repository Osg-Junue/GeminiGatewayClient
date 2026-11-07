# GeminiGatewayClient 探索的テスト観点仕様書
(GeminiGatewayClient Exploratory Testing Perspectives)

**ドキュメントバージョン**: 1.1.0  
**対象バージョン**: 4.1.0  
**対象プロジェクト**: `GeminiGatewayClient`  
**検証対象テストプロジェクト**: `tests/GeminiGatewayClient.ExploratoryTests`  
**関連プロジェクト**: `tests/GeminiGatewayClient.Tests` (単体テスト)

---

## 1. 探索的テストの目的と検証方針

### 1.1 探索的テストの目的と位置づけ

`GeminiGatewayClient` は、Google の Gemini REST API および Microsoft の統一 AI 抽象インターフェース `Microsoft.Extensions.AI` (MEAI) を高効率・ステートレスに利用するための .NET 10 ライブラリです。

本ソリューションにおけるテスト体系は、以下の 2 段階で構成されています：

1. **単体テスト (`tests/GeminiGatewayClient.Tests`)**:
   - `MockHttpMessageHandler` 等を用いた決定論的なテスト。
   - クライアントの電文シリアライズ、ヘッダー付与、URL 構築、SSE の読み取りの境界値などを高速・オフラインに検証。
2. **探索的テスト (`tests/GeminiGatewayClient.ExploratoryTests`)**:
   - 実際の Google Gemini API エンドポイント（デフォルト: `gemini-3-flash-preview`）に対して直接 HTTP リクエストを発行し、実 API の生きた挙動とクライアントの統合性を確認するライブ検証スイート。

#### 探索的テストが果たす中核的価値
- **実 API のプロトコル互換性保証**:
  モックでは見落とされがちな、Google API サーバー側の厳格なバリデーション（例: 引数なしの関数で空の `properties` を持つスキーマを送ると HTTP 400 になることがある点や、Thought Signature の整合性検証など）を実環境で確認します。
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
| **テスト対象モデル** | `GeminiTestModel` | `GEMINI_TEST_MODEL` | 1. `EnvironmentVariableTarget.Process`<br>2. `EnvironmentVariableTarget.User` (Windows のみ)<br>3. デフォルト値 (`gemini-3-flash-preview`) |

Windows ユーザー環境変数をフォールバック先としてサポートしているため、ターミナルや IDE 再起動後も API キーを安全に保持しつつ、Git コミット等のリポジトリ資産には一切クレデンシャルを含めない運用が可能です。

#### 1.2.3 直列実行（Non-Parallelization）の強制
Gemini API の無料枠・プレビューモデルには 1 分間あたりのリクエスト数（RPM）およびトークン数（TPM）のレートリミットが存在します。探索的テストアセンブリ全体で直列実行を強制しています：
- アセンブリレベル: `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
- コレクションレベル: `[CollectionDefinition(CollectionName, DisableParallelization = true)]`
これにより、テストの並行実行が原因の HTTP 429 (`RESOURCE_EXHAUSTED`) を避けています。直列でも、利用枠の状況によっては 429 が発生することがあります。

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
  RIFF/WAVE フォーマットに準拠した 44 バイトの標準ヘッダーと、16-bit リニア PCM モノラルの音声バイト列を生成。16kHz、1 秒間の 440Hz の正弦波を算出して使用。
- **文書データ (PDF)**:
  PDF 1.4 仕様に準拠したテキストレンダリング可能な最小構造のバイナリを動的生成。
  - ヘッダー: `%PDF-1.4`
  - オブジェクト 1: `/Type /Catalog /Pages 2 0 R`
  - オブジェクト 2: `/Type /Pages /Kids [3 0 R] /Count 1`
  - オブジェクト 3: `/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >>`
  - オブジェクト 4: コンテンツストリーム（`BT /F1 14 Tf 50 700 Td (...) Tj ET`）
  - オブジェクト 5: 標準 Type 1 フォント `/Type /Font /Subtype /Type1 /BaseFont /Helvetica`
  - 相互参照テーブル (`xref`) および `trailer`、`startxref`、`%%EOF`

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
[ GeminiClient / HTTP ]         (Gemini REST API)
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
     - 複数の対象を同時に調べる質問（TC-MEAI-01 では札幌と福岡）で、必要なツールがすべて実行されること。1 ターンで返るかどうかはモデルに依存するため、テストでは確認しません。
     - `FunctionInvokingChatClient` がすべてのツールを実行し、結果をモデルへ返すこと。
     - モデルがすべての結果を統合した最終回答を出力すること。

#### 2.1.2 引数なし関数 (Parameterless Functions) の仕様と検証
- **背景と課題**:
  ツールに引数がない場合、空の `properties` を持つスキーマ `{ "type": "object", "properties": {} }` は、Gemini に HTTP 400 で拒否されたことがあります。
- **クライアント実装仕様**:
  `ChatOptionsMapper.cs` の `ToParametersSchema` は、`properties` が空のスキーマでは `FunctionDeclaration.ParametersJsonSchema` を `null` にします。
- **検証ポイント**:
  - `GetSystemStatus` のように、引数を持たない C# 関数を登録。
  - `FunctionInvokingChatClient` 経由で実行した際、HTTP 400 エラーが発生せず正常にツールが呼び出され、返却値が回答に反映されること。

#### 2.1.3 複雑な型・列挙型・コレクションのスキーマ生成とバインディング
実アプリケーションでは、ツールは単純な文字列や数値だけでなく、構造化されたオブジェクトやコレクションを受け取ります。
- **検証対象の型バリエーション**:
  1. **C# `enum`**: `enum SupportPriority { Low, Normal, High, Urgent }`。モデルが選んだ値が C# 側の引数に正しく渡ること。
  2. **コレクション型**: `List<string> Tags`。モデルが生成した配列が正しく渡ること。
  3. **複合型**: `TicketFilter` レコード。
  4. **デフォルト値を持つ引数**: `int MaxCount = 5`。プロンプトで指定した値 `3` が渡ること。
- **戻り値のオブジェクトラップ仕様**:
  Gemini API のツールのレスポンス電文 (`FunctionResponse.response`) は **JSON Object** です。
  `GeminiGatewayClient` の `ToolJsonConverter.ToResponseObject` は、非オブジェクトの戻り値を自動的に `{"result": value}` という形式のオブジェクトにラップして送信します。このラップ自体は単体テストで確認します。

#### 2.1.4 エラーハンドリングとモデルリカバリ (Error Handling & Recovery)
外部 API やデータベース障害により、ツール内部でエラーが発生する場合があります。
- **検証シナリオ**:
  - `find_user_profile("USER-999")` が、`status`、`errorCode`、`message` を持つエラー情報を返却する。代替として `find_fallback_contact` も登録する。
  - `FunctionInvokingChatClient` はそのエラー情報を Gemini に戻す。
  - **期待されるモデルの振る舞い**:
    例外で終了せず、モデルがエラーを踏まえて、見つからなかったことや代替の連絡先を回答できること。

---

### 2.2 ストリーミング (`GetStreamingResponseAsync`) でのツール呼び出しとトークン集計

ストリーミングとファンクションコールを併用する場合、以下の重要なライフサイクルが発生します：
1. **ツール呼び出しフェーズ**:
   モデルのツール呼び出しが、`FunctionCallContent` を含む `ChatResponseUpdate` として発行される。
2. **自動実行とループバック**:
   `FunctionInvokingChatClient` がツールを実行し、その結果を持って再度モデルにリクエストを発行する。
3. **最終回答ストリーミング**:
   モデルがツール結果を踏まえたテキスト回答をストリーミング出力する。
4. **トークン二重計上の防止仕様**:
   `GeminiChatClient.cs` の `StreamCoreAsync` では、ストリーミング中の各チャンクで中間トークンを重複加算するのを防ぐため、ストリームの**末尾の単一更新**にのみ累積の `UsageContent` を付与して出力します。
- **検証ポイント**:
  - `IChatClient.GetStreamingResponseAsync` を `UseFunctionInvocation()` と併用した際、ツールの結果を反映した回答テキストを受信できること。
  - 全ストリーム終了後に得られる `ChatResponse` で `Usage.TotalTokenCount` が 0 より大きいこと。二重計上の防止は単体テストで確認します。
  - ストリーミングの応答を `ToChatResponse()` で結合しても `thoughtSignature` が失われないこと。

---

### 2.3 ChatClientBuilder によるパイプライン構築

`ChatClientBuilder` は ASP.NET Core のミドルウェアと同様に、複数の `DelegatingChatClient` をスタックして強力な処理パイプラインを構成します。

```csharp
// DelegatingChatClient の実装で包むと、FunctionInvokingChatClient の内側に置かれ、往復ごとに呼ばれる
var telemetry = new TelemetryTrackingDelegatingChatClient(geminiChatClient);

IChatClient client = new ChatClientBuilder(telemetry)
    .UseFunctionInvocation()          // 自動ファンクション呼び出し
    .Build();
```

- **検証観点**:
  1. **多段ミドルウェアの連鎖実行**:
     `DelegatingChatClient` を `FunctionInvokingChatClient` の内側に置き、ツール呼び出しの往復ごとに呼ばれて、回数とトークン数を記録できること。
  2. **`GetService<T>` の透過性 (Service Provider Transparency)**:
     最外層のラッパーから `client.GetService<IGeminiClient>()` や `client.GetService<ChatClientMetadata>()` を呼び出した際に、パイプラインを透過して下位のターゲットインスタンスが正しく取得できること。

---

### 2.4 構造化出力・マルチモーダル・ThinkingConfig（Thinking）とのツール併用

1. **構造化出力 (`ChatResponseFormat.ForJsonSchema`)**:
   `ChatOptions.ResponseFormat` に指定した JSON スキーマが `GenerationConfig.ResponseJsonSchema` に変換されること。実 API では未検証です（第 5 章）。
2. **マルチモーダル入力 (`DataContent`)**:
   `ChatMessage` の `DataContent` が `inlineData` のパートに変換されること。実 API では未検証です（第 5 章）。
3. **ThinkingConfig (Thinking) とファンクションコールの併用**: `ChatOptions.Reasoning` でも指定できますが、探索的テストは `RawRepresentationFactory` のみを使います（第 5 章）。
   - `RawRepresentationFactory` で `ThinkingConfig`（`ThinkingLevel.Low`、`IncludeThoughts = true`）を指定し、`UseFunctionInvocation()` を有効化。
   - **Thought Signature の保持**:
     `FunctionInvokingChatClient` が往復する間 `thoughtSignature` が維持され、署名の無効や欠落を示す HTTP 400 が発生しないこと。
4. **ToolMode の強制制御**: `None` と `RequireAny` は実 API では未検証です（第 5 章）。
   - `ChatToolMode.Auto`: 通常の自動判断。
   - `ChatToolMode.None`: ツールが登録されていても一切のツール呼び出しを禁止し、テキスト回答を強制する。
   - `ChatToolMode.RequireAny`: いずれかのツールを必ず呼び出すよう強制する。

---

## 3. GeminiGatewayClient ネイティブ機能のテスト観点体系

`GeminiGatewayClient` の核となる `IGeminiClient` / `GeminiClient` は、Gemini REST API の生成と埋め込みを直接操作するインターフェースです。

---

### 3.1 マルチモーダルと MediaResolution 制御

Gemini API は、画像だけでなく音声、PDF ドキュメント、ソースコードなどの多様なメディア形式をネイティブにサポートしています。

```
[ In-Memory Byte Synthesis ]
  ├── PNG (RFC 2083)  ──► Part.FromBytes(data, MimeTypes.ImagePng)
  ├── WAV (PCM 16bit) ──► Part.FromBytes(data, MimeTypes.AudioWav)
  └── PDF (PDF-1.4)   ──► Part.FromBytes(data, MimeTypes.Pdf)
                                │
                                ▼
                   [ GenerateContentRequest ]
                     ├── Contents: [ Content(Role.User, parts) ]
                     └── GenerationConfig: MediaResolution (Low / Medium / High)
```

#### 3.1.1 メディア種別ごとの検証観点
1. **音声入力 (`audio/wav`)**:
   - 16-bit リニア PCM WAV データをインメモリで生成し、`Part.FromBytes(..., MimeTypes.AudioWav)` として送信。
   - モデルが音声の特徴を説明し、プロンプトトークンが計上されること。
2. **文書入力 (`application/pdf`)**:
   - `ALPHA-7729` という文字列を含む最小構造の PDF バイナリをインメモリで生成し、`Part.FromBytes(..., MimeTypes.Pdf)` として送信。
   - モデルの回答に `ALPHA-7729` が含まれること。
3. **テキスト/コードファイル入力 (`text/plain`, `text/x-python`, `text/csv`)**: 現在テストはありません（第 5 章）。

#### 3.1.2 MediaResolution 制御とトークン消費量の検証
- `GenerationConfig.MediaResolution` には `Low`、`Medium`、`High` を指定できます。テストではトークン数の固定値を確認しません。
- **検証ポイント**:
  同一の 512x512 のインメモリ画像に対して `MediaResolution.Low` と `MediaResolution.Medium` をそれぞれ指定して `GenerateContentAsync` を呼び出し、レスポンスの `UsageMetadata.PromptTokenCount` を比較。
  `Low` のプロンプトトークン数が `Medium` より少ないことを検証。

---

### 3.2 サーバーサイド Python コード実行 (`Tool.CodeExecution`)

Gemini は、モデル自身が Python コードを記述し、Google が管理するセキュアなサンドボックス環境で実行してその計算結果を回答に利用する「Code Execution」ツールをネイティブで提供しています。

#### 3.2.1 リクエストとレスポンスの電文構造
- **リクエスト設定**:
  ```csharp
  var request = new GenerateContentRequest
  {
      Contents = [Content.FromText("Python で 1 から 100 までの 7 の倍数の合計を計算してください。")],
      Tools = [new Tool { CodeExecution = new CodeExecutionTool() }]
  };
  ```
- **レスポンス Part の構成**:
  モデルがコード実行を行った場合、返却される `Candidate.Content.Parts` には以下の Part が含まれます：
  1. `Part.ExecutableCode`:
     - `Language`: `CodeExecutionLanguage.Python`
     - `Code`: モデルが生成した Python スクリプト
  2. `Part.CodeExecutionResult`:
     - `Outcome`: `CodeExecutionOutcome.Ok`（または `Failed`, `DeadlineExceeded`）
     - `Output`: サンドボックス内の `stdout` / `stderr` 出力テキスト
  3. `Part.Text`:
     - コード実行結果を踏まえた、ユーザー向けの最終的な自然言語回答。

#### 3.2.2 検証ポイント
- 1 から 100 までの 7 の倍数の合計を、コード実行ツールで求めるよう指示。
- レスポンスの `Candidate.Content.Parts` 内に `ExecutableCode` および `CodeExecutionResult` が含まれていることを検証。
- `ExecutableCode.Code` に有効な Python コードが含まれ、`CodeExecutionResult.Outcome == CodeExecutionOutcome.Ok` であることを確認。
- 最終テキストに正解の `735` が含まれていること。

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
  - 実 API がネイティブのスキーマ定義を受理すること。テストでは `companyName`、`foundedYear`、`isPublic`、`headquarters` を持つスキーマを使います。
  - 応答を C# のレコードへ `JsonSerializer.Deserialize<T>` で変換でき、値が妥当であること。

---

### 3.4 コンテキストキャッシュ (`CachedContent`)

Gemini API では、大量のプロンプト（ドキュメント集、コードベース、長いシステム指示）をサーバー側に事前キャッシュし、以降のリクエストで `cachedContent` リソース名を参照することで、レイテンシと課金トークン数を削減する機能が提供されています。

#### 3.4.1 スコープ境界とクライアントの役割
`docs/API_COMPATIBILITY.md` に明記されている通り、ステートレスクライアントである `GeminiGatewayClient` は**キャッシュの管理エンドポイント（作成・更新・削除・一覧）を意図的にサポート外**としています。しかし、生成リクエストにおける**キャッシュの消費（参照）**はファーストクラスでサポートしています。

- **リクエスト**: `GenerateContentRequest.CachedContent = "cachedContents/sample-cache-id"`
- **レスポンス**: `UsageMetadata.CachedContentTokenCount`（キャッシュから読み出されたプロンプトトークン数）
- **MEAI 連携**: `ChatOptions.RawRepresentationFactory` の `GenerateContentRequest.CachedContent` からのリクエストマッピングおよび `Usage.CachedInputTokenCount` への透過的マッピング。

#### 3.4.2 探索的テストでの検証アプローチ
キャッシュの作成には管理エンドポイントが必要で、このライブラリの対象外です。そのため、探索的テストでは実際のキャッシュを作成しません。
検証は以下のように分かれています：
1. **探索的テスト**: 存在しないキャッシュ名を指定すると、`GeminiApiException`（HTTP 400、403、404 のいずれか）になり、メッセージに `CachedContent` が含まれること。これで値がサーバーまで届いていることを確認します。
2. **単体テスト**: `UsageMetadata.CachedContentTokenCount` が MEAI の `CachedInputTokenCount` に反映されること。

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
  - `GenerateContentResponse.IsBlocked`: プロンプトまたは最初の候補がブロックされたかを示すプロパティ。
- **検証ポイント**:
  閾値を指定したリクエストが受け入れられ、無害なプロンプトがブロックされず、`SafetyRatings` が返ること。

---

### 3.6 ツール制御とグラウンディング

#### 3.6.1 FunctionCallingMode による強制制御
`ToolConfig.FunctionCallingConfig` は、モデルのツール呼び出し動作を強制制御します：
- `FunctionCallingMode.Auto`: モデルが必要に応じてツールの呼び出しを判断（デフォルト）。
- `FunctionCallingMode.Any`: 登録されたツールのうち、いずれかのツール呼び出しを強制。
- `FunctionCallingMode.None`: **ツール定義が存在する場合であっても、ツール呼び出しを厳格に禁止し、通常の自然言語テキスト回答を強制する**。
- **検証ポイント (`FunctionCallingMode.None`)**:
  天気を取得するツールを宣言し、`FunctionCallingMode.None` を指定。通常ならツールを呼ぶ質問でも、`FunctionCall` が 0 個で、テキストの回答が返ることを検証。

#### 3.6.2 グラウンディング統合
- `Tool.GoogleSearch` (`GoogleSearchTool`):
  最新の出来事に関する質問に対し、Google 検索を実行。`Candidate.GroundingMetadata` に検索クエリ（`WebSearchQueries`）や参照チャンク（`GroundingChunks`）が返却されること。
- `Tool.UrlContext` (`UrlContextTool`):
  指定された URL の内容を取得してグラウンディング情報を付与すること。

---

## 4. 探索的テストの一覧

`tests/GeminiGatewayClient.ExploratoryTests` には 9 クラス、45 件のテストがあります。すべて `[ExploratoryFact]` で定義されています。この表はテストコードが何を確認するかを示すもので、最新の実行結果を示すものではありません。

TC 番号は、テスト開始時に `LogSection` が出力する番号です。番号のないテストもあります。

### 4.1 `BasicGenerationExploratoryTests` (7 件)

| テストメソッド | 主な確認内容 |
|:---|:---|
| `ModelDiscovery_ListsAvailableFlashModels_AndSelectsPreferredFlashModel` | モデル一覧を取得でき、対象モデルが決まること |
| `SimplePrompt_GenerateContentAsync_ReturnsTextAndUsageMetadata` | テキスト、`FinishReason.Stop`、トークン数が返ること |
| `Streaming_YieldsChunksInRealTime` | チャンクが届き、`FinishReason.Stop` とトークン数が含まれること |
| `StructuredOutput_ResponseJsonSchema_DeserializesToStronglyTypedRecord` | `ResponseJsonSchema` の出力をレコードへ変換できること |
| `ThinkingConfig_IncludeThoughts_ReturnsThoughtSummaryAndFinalAnswer` | `ThinkingConfig` を指定して最終回答を得られること |
| `ThinkingConfig_ExhaustedMaxOutputTokens_HandlesEmptyPartsSafelyWithTryGetModelContent` | 出力上限で `FinishReason.MaxTokens` になり、その後の再リクエストで回答を得られること |
| `Multimodal_InlinePngBytes_RecognizesImageColorAndShape` | 32x32 の PNG の内容を説明できること |

### 4.2 `ChatClientIntegrationExploratoryTests` (11 件)

| TC | テストメソッド | 主な確認内容 |
|:---:|:---|:---|
| - | `IChatClient_UseFunctionInvocation_AutomaticallyChainsFourLocalTools` | 4 つのツールが実行され、結果が回答に反映されること |
| - | `IChatClient_UseFunctionInvocation_Streaming_StreamsFinalResponseAndSingleUsage` | ストリーミングで 2 回以上ツールが実行され、回答と `Usage` を得られること |
| - | `IChatClient_MultiTurnTools_WithStronglyTypedReturnSchema_CollidingCallIds_AndRedactedArguments_Succeeds` | 戻り値スキーマ、再利用された `CallId`、伏せた引数を含む複数ターンが成功すること |
| - | `IChatClient_ThinkingConfig_ReturnsReasoningContent` | `RawRepresentationFactory` の `ThinkingConfig` で推論トークンまたは推論内容を得られること |
| - | `DependencyInjection_AddGeminiClient_WithDynamicApiKeyProviderAndCustomHeaders_WorksEndToEnd` | DI 登録したクライアントで `ApiKeyProvider` が呼び出しごとに使われること |
| TC-MEAI-01 | `IChatClient_UseFunctionInvocation_ParallelToolCalls_ExecutesConcurrentlyAndSynthesizesAnswer` | 札幌と福岡の両方についてツールが実行され、回答に反映されること |
| TC-MEAI-02 | `IChatClient_UseFunctionInvocation_ComplexParametersAndEnums_BindsCorrectly` | レコード、列挙型、リストの引数が正しく渡ること |
| TC-MEAI-03 | `IChatClient_UseFunctionInvocation_ToolErrorPayload_ModelRecoversGracefully` | ツールのエラー内容を受けて、モデルが回答を返すこと |
| TC-MEAI-04 | `IChatClient_ChatClientBuilder_MultiMiddlewarePipeline_TracksTelemetryAndPreservesGetService` | ミドルウェアが 2 回以上呼ばれ、`GetService<T>` が機能すること |
| TC-MEAI-05 | `IChatClient_UseFunctionInvocation_CombinedWithThinking_PreservesThoughtSignatures` | `ThinkingConfig` とツールの併用で、署名エラーなく回答を得られること |
| TC-MEAI-06 | `IChatClient_UseFunctionInvocation_ParameterlessFunction_ExecutesWithoutSchemaRejection` | 引数なしの関数が実行されること |

### 4.3 `NativeCapabilitiesExploratoryTests` (8 件)

| TC | テストメソッド | 主な確認内容 |
|:---:|:---|:---|
| TC-NAT-01 | `Multimodal_InlineWavAudioBytes_RecognizesAudioSignal` | 16kHz、1 秒、440Hz の WAV について説明できること |
| TC-NAT-02 | `Multimodal_InlinePdfDocumentBytes_ExtractsStructuredContent` | PDF から `ALPHA-7729` を読み取れること |
| TC-NAT-03 | `Multimodal_MediaResolution_LowVsMedium_DemonstratesDifferentialTokenConsumption` | 512x512 の PNG で `Low` のプロンプトトークンが `Medium` より少ないこと |
| TC-NAT-04 | `NativeTools_ServerSidePythonCodeExecution_ExecutesAlgorithmAndReturnsResult` | Python が実行され、回答に `735` が含まれること |
| TC-NAT-05 | `StructuredOutput_NativeOpenApiSchema_ValidatesAndDeserializesContract` | `ResponseSchema` の出力をレコードへ変換できること |
| TC-NAT-06 | `SafetySettings_CustomThresholds_ReturnsPopulatedSafetyRatings` | 閾値を指定した無害な質問がブロックされず、`SafetyRatings` が返ること |
| TC-NAT-07 | `ToolConfig_FunctionCallingModeNone_SuppressesDeclaredTools` | `FunctionCallingMode.None` で `FunctionCall` が 0 個になること |
| TC-NAT-08 | `ContextCaching_CachedContentWirePropagation_TransmitsResourceIdentifier` | 存在しないキャッシュで 400、403、404 のいずれかになり、メッセージに `CachedContent` が含まれること |

### 4.4 その他のテストクラス (19 件)

| クラス | テストメソッド | 主な確認内容 |
|:---|:---|:---|
| `FunctionCallingChainExploratoryTests` | `ComplexToolChain_ParallelAndSequentialCalls_CompletesBusinessTripWorkflow` | 3 ラウンド以上で 5 種類のツールが呼ばれ、回答に結果が含まれること |
| | `ToolConfig_FunctionCallingModeAny_ForcesAllowedFunctionCall` | `FunctionCallingMode.Any` で指定した関数だけが呼ばれること |
| `GroundingAndEmbeddingsExploratoryTests` | `GoogleSearchGrounding_NativeGeminiClient_PopulatesGroundingMetadataAndChunks` | `GroundingMetadata` が返ること |
| | `GoogleSearchGrounding_IChatClient_HostedWebSearchTool_MapsCitationAnnotationsAndValidatesMultibyteSpans` | 引用の範囲がテキストの長さに収まること |
| | `UrlContext_NativeGeminiClient_RetrievesUrlAndPopulatesUrlContextMetadata` | `UrlContextMetadata` が返ること |
| | `EmbedContent_AndBatchEmbedContents_NativeGeminiClient_ComputesExpectedSemanticSimilarity` | 単一とバッチの埋め込みの次元数と類似度 |
| | `IEmbeddingGenerator_ViaDependencyInjection_SingleAndBatchGeneration_WorksEndToEnd` | DI 登録した `IEmbeddingGenerator` の単一とバッチの生成 |
| `GroundingOffsetsExploratoryTests` | `NonStreaming_RegionsMatchTheSegmentText` | 引用の範囲が Gemini の示す `Segment.Text` と一致すること |
| | `Streaming_RegionsMatchTheSegmentText` | ストリーミングでも同様に一致すること |
| | `ToolModeWithoutFunctionDeclarations_IsAccepted` | 関数なしで `ChatToolMode.Auto` を指定しても受け付けられること |
| `LongConversationExploratoryTests` | `MultiTurn_NativeGeminiClient_RetainsAllConstraintsAcrossSixTurns` | 6 ターンの文脈が保たれ、プロンプトトークンが増えること |
| | `MultiTurn_IChatClient_WithSimulatedJsonPersistence_ContinuesCleanlyAcrossTurns` | JSON に保存して復元した履歴で会話を続けられること |
| | `MultiTurn_IChatClient_Streaming_PreservesThoughtSignatureAcrossToChatResponse_AndOmitsEmptyTextContent` | `ToChatResponse()` を経ても署名が残ること |
| | `MultiTurn_NativeStreaming_ToModelContentAsync_KeepsPartsForSubsequentTurns` | `ToModelContentAsync()` の結果で会話を続けられること |
| `RequestEncodingExploratoryTests` | `Generate_JapaneseSentAsUtf8_ReachesTheModelUnchanged` | 日本語などが UTF-8 で送られ、そのまま届くこと |
| | `ChatStream_JapaneseSentAsUtf8_ReachesTheModelUnchanged` | `IChatClient` のストリーミングでも同様であること |
| | `Embedding_JapaneseSentAsUtf8_IsAccepted` | 埋め込みでも UTF-8 で送られ、受け付けられること |
| `StreamFormatExploratoryTests` | `Stream_WithoutAltSse_ReadsTheJsonArrayGeminiSends` | `alt=sse` が届かない場合の JSON 配列を読めること |
| | `Stream_WithServerSentEventsOff_ReadsTheJsonArrayGeminiSends` | `UseServerSentEvents = false` でも同様に読めること |

---

## 5. 実 API で未検証の観点

次の観点には、探索的テストがありません。単体テストがある観点は、モック応答での確認に限られます。

| 観点 | 現在の確認範囲 |
|:---|:---|
| `ChatOptions.Reasoning` | 単体テストのみ。探索的テストは `RawRepresentationFactory` の `ThinkingConfig` を使用 |
| `ChatResponseFormat.ForJsonSchema` | 単体テストのみ。探索的テストはネイティブのスキーマ指定のみ |
| `IChatClient` への `DataContent` 入力 | 単体テストのみ。探索的テストは `Part.FromBytes` のみ |
| `ChatToolMode.None`、`RequireAny` | 単体テストのみ。探索的テストはネイティブの `FunctionCallingMode` と、関数なしの `ChatToolMode.Auto` のみ |
| 有効なキャッシュでの `CachedContentTokenCount` | `CachedInputTokenCount` への変換は単体テストのみ。実 API では存在しないキャッシュの拒否だけを確認 |
| ストリーミングの `Usage` の二重計上防止 | `UsageContent` の出力は単体テストで確認。実 API では `TotalTokenCount > 0` のみを確認 |
| 1 ターンでの並列ツール呼び出し | TC-MEAI-01 は全呼び出しの実行だけを確認し、同じターンで返ることは確認していない |
| `MediaResolution.High` | 単体テストはシリアライズのみ。探索的テストは `Low` と `Medium` のみ |
| CSV、Python など、テキストファイルのインライン入力 | テストなし |
| 有害な入力の実際のブロック | ブロック判定は単体テストのみ。TC-NAT-06 は無害な入力のみ |
| 他社の履歴を Gemini へ渡す場合 | 探索的テストなし。[`IChatClient` の説明](usage/chat-client.md#histories-from-other-providers)にある動作は手動で確認 |

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

# 2. 既存単体テストの全件成功確認（リグレッション防止）
dotnet test tests/GeminiGatewayClient.Tests/GeminiGatewayClient.Tests.csproj

# 3. 探索的テストの実行（GeminiAPIKey または GEMINI_API_KEY があれば実 API を実行、なければ全件スキップ）
dotnet test tests/GeminiGatewayClient.ExploratoryTests/GeminiGatewayClient.ExploratoryTests.csproj
```
