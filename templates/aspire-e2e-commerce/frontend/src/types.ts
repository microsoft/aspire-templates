export type Status = "PaymentPending" | "PaymentRetryScheduled" | "PaymentDeclined" | "PaymentAuthorized" | "Picking" | "Packed" | "InTransit" | "Delivered";
export type Product = { id: string; name: string; priceCents: number; stock: number };
export type Order = {
  id: string; productId: string; quantity: number; customerId: string; mockOutcome: string;
  status: Status; version: number; attempt: number; createdAt: string; updatedAt: string; nextAt: string | null; traceId: string;
};
export type OrderEvent = { sequence: number; orderId: string; status: Status; service: string; detail: string; traceId: string; spanId: string; messageId: string; at: string };
export type Detail = { order: Order; events: OrderEvent[] };
export type Snapshot = { products: Product[]; orders: Order[]; customers: string[]; serverTime: string; dashboardUrl: string | null };
export const statusLabel = (status: Status) => ({
  PaymentPending: "Awaiting payment", PaymentRetryScheduled: "Payment retry", PaymentDeclined: "Payment declined",
  PaymentAuthorized: "Payment authorized", Picking: "Picking inventory", Packed: "Ready for dispatch",
  InTransit: "On the road", Delivered: "Delivered"
})[status];
export const customerLabel = (id: string) => id.replace("-house", "").replace(/^./, c => c.toUpperCase()) + " house";
export const shortId = (id: string) => id.slice(0, 8).toUpperCase();
