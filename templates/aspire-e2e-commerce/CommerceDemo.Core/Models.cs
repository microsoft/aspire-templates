using System.Diagnostics;

namespace CommerceDemo.Core;

public sealed record Product(string Id, string Name, int PriceCents, int Stock);
public sealed record OrderRequest(string ProductId, int Quantity, string CustomerId, string MockOutcome);
public sealed record Order(
    Guid Id, string ProductId, int Quantity, string CustomerId, string MockOutcome,
    string Status, int Version, int Attempt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAt, string TraceId);
public sealed record OrderEvent(
    long Sequence, Guid OrderId, string Status, string Service, string Detail,
    string TraceId, string SpanId, Guid MessageId, DateTimeOffset At);
public sealed record OrderDetail(Order Order, IReadOnlyList<OrderEvent> Events);
public sealed record WorkMessage(Guid Id, Guid OrderId, int Version, string TraceParent, string? TraceState);
public sealed record Transition(string Status, string Detail, string? Queue, int DelaySeconds, bool ReleaseStock = false);

public static class CommerceFlow
{
    public static readonly string[] Customers = ["harbor-house", "garden-house", "hill-house"];
    public static readonly string[] Outcomes = ["approved", "declined", "retry-once"];
    public static readonly ActivitySource Traces = new("CommerceDemo.Business");

    public static Transition? Advance(Order order, string role) => (role, order.Status) switch
    {
        ("payments", "PaymentPending") when order.MockOutcome == "declined" =>
            new("PaymentDeclined", "Mock payment declined. Reserved stock released; retry is available.", null, 0, true),
        ("payments", "PaymentPending") when order.MockOutcome == "retry-once" =>
            new("PaymentRetryScheduled", "Simulated gateway timeout. Durable retry scheduled; no charge was made.", "payments", 4),
        ("payments", "PaymentPending" or "PaymentRetryScheduled") =>
            new("PaymentAuthorized", "Mock authorization accepted. No real payment was taken.", "fulfilment", 3),
        ("fulfilment", "PaymentAuthorized") =>
            new("Picking", "Warehouse picker collecting reserved inventory.", "fulfilment", 4),
        ("fulfilment", "Picking") =>
            new("Packed", "One parcel packed and handed to the shipping depot.", "shipping", 3),
        ("shipping", "Packed") =>
            new("InTransit", "Delivery truck departed. Travel time is simulated.", "shipping", 12),
        ("shipping", "InTransit") =>
            new("Delivered", "Parcel delivered to the synthetic customer.", null, 0),
        _ => null
    };

    public static bool Valid(OrderRequest request) =>
        request.Quantity is >= 1 and <= 5 && Customers.Contains(request.CustomerId)
        && Outcomes.Contains(request.MockOutcome) && request.ProductId is "lamp" or "chair" or "plant";
}

public sealed class CommerceConflictException(string message) : Exception(message);
