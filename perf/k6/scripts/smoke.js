import http from "k6/http";
import { check, sleep } from "k6";

const baseUrl = __ENV.BASE_URL || "http://localhost:8080";

export const options = {
  vus: 1,
  duration: __ENV.SMOKE_DURATION || "60s",
  thresholds: {
    http_req_duration: ["p(95)<500"],
    http_req_failed: ["rate<0.01"],
  },
};

export default function () {
  const payload = JSON.stringify({
    model: __ENV.MODEL || "gpt-local",
    stream: false,
    messages: [{ role: "user", content: "k6 smoke ping" }],
  });

  const response = http.post(`${baseUrl}/v1/chat/completions`, payload, {
    headers: { "Content-Type": "application/json" },
    tags: { name: "chat_completions" },
  });

  check(response, {
    "status is 200": (r) => r.status === 200,
    "has completion id": (r) => r.body && r.body.includes("chatcmpl"),
  });

  sleep(1);
}
