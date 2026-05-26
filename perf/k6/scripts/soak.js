import http from "k6/http";
import { check, sleep } from "k6";
import { chatCompletionPayload, jsonHeaders } from "../lib/helpers.js";

const baseUrl = __ENV.BASE_URL || "http://localhost:8080";
const model = __ENV.MODEL || "gpt-local";

export const options = {
  vus: Number(__ENV.SOAK_VUS || 5),
  duration: __ENV.SOAK_DURATION || "4h",
  thresholds: {
    http_req_failed: ["rate<0.01"],
    http_req_duration: ["p(99)<60000"],
  },
};

export default function () {
  const response = http.post(
    `${baseUrl}/v1/chat/completions`,
    chatCompletionPayload(model, false),
    { headers: jsonHeaders(), tags: { name: "chat_completions_soak" } },
  );

  check(response, {
    "status is 200": (r) => r.status === 200,
  });

  sleep(Number(__ENV.SOAK_SLEEP_SEC || 2));
}
