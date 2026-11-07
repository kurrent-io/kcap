using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>One thing a brief leaves out, citing a turn the caller can read.</summary>
public record WhatElseFindingDto(
    [property: JsonPropertyName("kind")]       string Kind,
    [property: JsonPropertyName("text")]       string Text,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("turn")]       int    Turn);
