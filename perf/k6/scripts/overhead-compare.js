import http from "k6/http";
import { check, sleep } from "k6";
import { chatCompletionPayload, jsonHeaders } from "../lib/helpers.js";

const directUrl = __ENV.DIRECT_URL || "http://127.0.0.1:18080";
const gatewayUrl = __ENV.GATEWAY_URL || "http://127.0.0.1:8080";
const model = __ENV.MODEL || "gpt-local";

export const options = {
  scenarios: {
    direct_upstream: {
      executor: "constant-vus",
      vus: Number(__ENV.OVERHEAD_VUS || 5),
      duration: __ENV.OVERHEAD_DURATION || "30s",
      exec: "hitDirect",
    },
    via_gateway: {
      executor: "constant-vus",
      vus: Number(__ENV.OVERHEAD_VUS || 5),
      duration: __ENV.OVERHEAD_DURATION || "30s",
      exec: "hitGateway",
      startTime: "5s",
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.02"],
  },
};

function postChat(baseUrl) {
  return http.post(
    `${baseUrl}/v1/chat/completions`,
    chatCompletionPayload(model, false),
    { headers: jsonHeaders(), tags: { name: "overhead_chat" } },
  );
}

export function hitDirect() {
  const response = postChat(directUrl);
  check(response, { "direct status 200": (r) => r.status === 200 });
  sleep(0.2);
}

export function hitGateway() {
  const response = postChat(gatewayUrl);
  check(response, { "gateway status 200": (r) => r.status === 200 });
  sleep(0.2);
}
