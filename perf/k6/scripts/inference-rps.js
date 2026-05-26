import http from "k6/http";
import { check, sleep } from "k6";
import { chatCompletionPayload, jsonHeaders } from "../lib/helpers.js";

const baseUrl = __ENV.BASE_URL || "http://localhost:8080";
const model = __ENV.MODEL || "gpt-local";
const apiKey = __ENV.API_KEY || "";

export const options = {
  stages: [
    { duration: "2m", target: 10 },
    { duration: "3m", target: 50 },
    { duration: "3m", target: 100 },
    { duration: "2m", target: 200 },
    { duration: "1m", target: 0 },
  ],
  thresholds: {
    http_req_duration: ["p(99)<60000"],
    http_req_failed: ["rate<0.01"],
    checks: ["rate>0.99"],
  },
};

export default function () {
  const headers = jsonHeaders();
  if (apiKey) {
    headers["X-API-Key"] = apiKey;
  }

  const response = http.post(
    `${baseUrl}/v1/chat/completions`,
    chatCompletionPayload(model, false),
    { headers, tags: { name: "chat_completions_rps" } },
  );

  check(response, {
    "status is 200": (r) => r.status === 200,
  });

  sleep(0.1);
}
