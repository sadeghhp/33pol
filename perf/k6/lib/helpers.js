export function jsonHeaders() {
  return { "Content-Type": "application/json" };
}

export function applyApiKeyAuth(headers, apiKey) {
  if (!apiKey) {
    return headers;
  }

  return {
    ...headers,
    Authorization: `Bearer ${apiKey}`,
  };
}

export function chatCompletionPayload(model, stream = false) {
  return JSON.stringify({
    model,
    stream,
    messages: [{ role: "user", content: "hello" }],
  });
}
