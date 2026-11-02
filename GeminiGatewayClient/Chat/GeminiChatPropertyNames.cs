namespace GeminiGatewayClient;

/// <summary>
/// Keys used in <c>AdditionalProperties</c> dictionaries by <see cref="GeminiChatClient"/>.
/// </summary>
public static class GeminiChatPropertyNames
{
    /// <summary>
    /// <c>ChatResponse.AdditionalProperties</c> / <c>ChatResponseUpdate.AdditionalProperties</c> key holding the prompt-level
    /// <see cref="Models.BlockReason"/> (as a string) when the input was blocked.
    /// </summary>
    public const string BlockReason = "gemini.blockReason";

    /// <summary>
    /// <c>FunctionCallContent.AdditionalProperties</c> key holding the <c>thoughtSignature</c> of a function call part.
    /// Stored here (not only in <c>RawRepresentation</c>) so it survives serialization of chat history; Gemini 3 models reject
    /// replayed function calls whose signature is missing.
    /// </summary>
    public const string ThoughtSignature = "gemini.thoughtSignature";

    /// <summary>
    /// <c>FunctionCallContent.AdditionalProperties</c> key set to <c>true</c> when <c>CallId</c> was generated locally because
    /// the server did not return a function call ID. Such IDs are never sent back to the API.
    /// </summary>
    public const string SynthesizedCallId = "gemini.synthesizedCallId";

    /// <summary>
    /// <c>AIContent.AdditionalProperties</c> key holding <see cref="Models.ExecutableCode.Language"/> so code execution parts
    /// survive chat history serialization when <c>RawRepresentation</c> is dropped.
    /// </summary>
    public const string ExecutableCodeLanguage = "gemini.executableCode.language";

    /// <summary>
    /// <c>AIContent.AdditionalProperties</c> key holding <see cref="Models.ExecutableCode.Code"/> so code execution parts
    /// survive chat history serialization when <c>RawRepresentation</c> is dropped.
    /// </summary>
    public const string ExecutableCode = "gemini.executableCode.code";

    /// <summary>
    /// <c>AIContent.AdditionalProperties</c> key holding <see cref="Models.CodeExecutionResult.Outcome"/> so code execution result parts
    /// survive chat history serialization when <c>RawRepresentation</c> is dropped.
    /// </summary>
    public const string CodeExecutionOutcome = "gemini.codeExecutionResult.outcome";

    /// <summary>
    /// <c>AIContent.AdditionalProperties</c> key holding <see cref="Models.CodeExecutionResult.Output"/> so code execution result parts
    /// survive chat history serialization when <c>RawRepresentation</c> is dropped.
    /// </summary>
    public const string CodeExecutionOutput = "gemini.codeExecutionResult.output";
}

