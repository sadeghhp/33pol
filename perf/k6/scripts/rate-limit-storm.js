import http from "k6/http";
import { check } from "k6";
import { applyApiKeyAuth, chatCompletionPayload, jsonHeaders } from "../lib/helpers.js";

const baseUrl = __ENV.BASE_URL || "http://localhost:8080";
const model = __ENV.MODEL || "gpt-local";
const apiKey = __ENV.API_KEY || "";

export const options = {
  vus: Number(__ENV.STORM_VUS || 20),
  duration: "2m",
  thresholds: {
    checks: ["rate>0.95"],
  },
};

export default function () {
  const headers = applyApiKeyAuth(jsonHeaders(), apiKey);

  const response = http.post(
    `${baseUrl}/v1/chat/completions`,
    chatCompletionPayload(model, false),
    { headers, tags: { name: "rate_limit_storm" } },
  );

  check(response, {
    "status is 200 or 429": (r) => r.status === 200 || r.status === 429,
    "429 has error code header when limited": (r) =>
      r.status !== 429 ||
      (r.headers["X-33pol-Error-Code"] || "").includes("rate_limit") ||
      (r.headers["X-33pol-Error-Code"] || "").includes("quota"),
  });
}
