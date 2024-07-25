using RabbitMQ.Client;

namespace BunnyBracelet.Tests
{
    internal sealed class BasicPropertiesMock : IBasicProperties
    {
        public string? AppId { get; set; }

        public string? ClusterId { get; set; }

        public string? ContentEncoding { get; set; }

        public string? ContentType { get; set; }

        public string? CorrelationId { get; set; }

        public byte DeliveryMode { get; set; }

        public string? Expiration { get; set; }

        public string? MessageId { get; set; }

        public bool Persistent { get; set; }

        public byte Priority { get; set; }

        public string? ReplyTo { get; set; }

        public PublicationAddress? ReplyToAddress { get; set; }

        public AmqpTimestamp Timestamp { get; set; }

        public string? Type { get; set; }

        public string? UserId { get; set; }

        public IDictionary<string, object>? Headers { get; set; }

        public ushort ProtocolClassId => 22;

        public string ProtocolClassName => "Test";

        public bool IsAppIdPresent() => AppId != default;

        public bool IsClusterIdPresent() => ClusterId != default;

        public bool IsContentEncodingPresent() => ContentEncoding != default;

        public bool IsContentTypePresent() => ContentType != default;

        public bool IsCorrelationIdPresent() => CorrelationId != default;

        public bool IsDeliveryModePresent() => DeliveryMode != default;

        public bool IsExpirationPresent() => Expiration != default;

        public bool IsMessageIdPresent() => MessageId != default;

        public bool IsPriorityPresent() => Priority != default;

        public bool IsReplyToPresent() => ReplyTo != default;

        public bool IsTimestampPresent() => Timestamp.UnixTime != default;

        public bool IsTypePresent() => Type != default;

        public bool IsUserIdPresent() => UserId != default;

        public bool IsHeadersPresent() => Headers != default;

        public void ClearAppId() => AppId = default;

        public void ClearClusterId() => ClusterId = default;

        public void ClearContentEncoding() => ContentEncoding = default;

        public void ClearContentType() => ContentType = default;

        public void ClearCorrelationId() => CorrelationId = default;

        public void ClearDeliveryMode() => DeliveryMode = default;

        public void ClearExpiration() => Expiration = default;

        public void ClearMessageId() => MessageId = default;

        public void ClearPriority() => Priority = default;

        public void ClearReplyTo() => ReplyTo = default;

        public void ClearTimestamp() => Timestamp = default;

        public void ClearType() => Type = default;

        public void ClearUserId() => UserId = default;

        public void ClearHeaders() => Headers = default;
    }
}
