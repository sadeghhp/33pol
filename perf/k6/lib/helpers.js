export function jsonHeaders() {
  return { "Content-Type": "application/json" };
}

export function chatCompletionPayload(model, stream = false) {
  return JSON.stringify({
    model,
    stream,
    messages: [{ role: "user", content: "hello" }],
  });
}
