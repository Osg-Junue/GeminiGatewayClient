namespace GeminiAPIWrapper;

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
}

