using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary><see cref="Outcome"/> is <c>found</c>, <c>nothing_to_add</c>, <c>not_released</c>, <c>unavailable</c>,
/// <c>busy</c> or <c>limited</c>; <see cref="Label"/> and <see cref="Points"/> carry only for <c>found</c>.</summary>
public record WhatElseDto(
    [property: JsonPropertyName("outcome")] string                    Outcome,
    [property: JsonPropertyName("label")]   string?                   Label,
    [property: JsonPropertyName("points")]  List<WhatElseFindingDto>? Points);
