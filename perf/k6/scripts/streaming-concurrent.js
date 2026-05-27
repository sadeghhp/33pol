import http from "k6/http";
import { check, sleep } from "k6";
import { chatCompletionPayload, jsonHeaders } from "../lib/helpers.js";

const baseUrl = __ENV.BASE_URL || "http://localhost:8080";
const model = __ENV.MODEL || "gpt-local";
const apiKey = __ENV.API_KEY || "";
const vus = Number(__ENV.STREAM_VUS || 50);

export const options = {
  scenarios: {
    streaming: {
      executor: "constant-vus",
      vus,
      duration: __ENV.STREAM_DURATION || "3m",
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.02"],
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
    chatCompletionPayload(model, true),
    { headers, tags: { name: "chat_completions_stream" }, timeout: "120s" },
  );

  check(response, {
    "status is 200": (r) => r.status === 200,
    "content-type is event-stream": (r) =>
      (r.headers["Content-Type"] || "").includes("text/event-stream"),
    "body has data chunks": (r) => r.body && r.body.includes("data:"),
  });

  sleep(Number(__ENV.K6_SLEEP_SEC || 0));
}
