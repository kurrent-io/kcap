using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>One inbox row. Mutations name it by logical id and the version the caller saw.</summary>
public sealed record NotificationItemDto {
    [JsonPropertyName("logical_id")]    public string                                     LogicalId    { get; init; } = "";
    [JsonPropertyName("type")]          public string                                     Type         { get; init; } = "";
    [JsonPropertyName("version")]       public long                                       Version      { get; init; }
    [JsonPropertyName("updated_at")]    public DateTimeOffset                             UpdatedAt    { get; init; }
    [JsonPropertyName("is_unread")]     public bool                                       IsUnread     { get; init; }
    [JsonPropertyName("title")]         public string                                     Title        { get; init; } = "";
    [JsonPropertyName("headline")]      public IReadOnlyList<NotificationRunDto>?         Headline     { get; init; }
    [JsonPropertyName("lines")]         public IReadOnlyList<IReadOnlyList<NotificationRunDto>>? Lines { get; init; }
    [JsonPropertyName("quote")]         public string?                                    Quote        { get; init; }
    [JsonPropertyName("chips")]         public IReadOnlyList<string>                      Chips        { get; init; } = [];
    [JsonPropertyName("status_line")]   public string?                                    StatusLine   { get; init; }
    [JsonPropertyName("link_url")]      public string?                                    LinkUrl      { get; init; }
    [JsonPropertyName("link_label")]    public string?                                    LinkLabel    { get; init; }
    [JsonPropertyName("link_external")] public bool                                       LinkExternal { get; init; }
    [JsonPropertyName("buttons")]       public IReadOnlyList<NotificationButtonDto>?      Buttons      { get; init; }
    [JsonPropertyName("reply_label")]   public string?                                    ReplyLabel   { get; init; }
}
