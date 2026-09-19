"""Local-inference sidecar stub (Task 043, OPTIONAL).

Minimal FastAPI stub for local testing: /health, /warmup, /infer echo mock
responses with the registry shape {modelName, modelVersion, artifactHash,
capability, payload, deviceProfile}. Production swaps real models without C#
changes. No secrets are logged or stored.
"""

from __future__ import annotations

import os

from fastapi import FastAPI, Response, status
from pydantic import BaseModel

app = FastAPI(title="local-inference-sidecar")

MODEL_NAME = os.environ.get("MODEL_NAME", "local-small")
MODEL_VERSION = os.environ.get("MODEL_VERSION", "1")
DEVICE_PROFILE = os.environ.get("DEVICE_PROFILE", "cpu")
ARTIFACT_HASH = os.environ.get("ARTIFACT_HASH", "unspecified")


class WarmupRequest(BaseModel):
    modelName: str = MODEL_NAME
    modelVersion: str = MODEL_VERSION
    deviceProfile: str = DEVICE_PROFILE


class InferRequest(BaseModel):
    modelName: str = MODEL_NAME
    modelVersion: str = MODEL_VERSION
    artifactHash: str = ARTIFACT_HASH
    capability: str = "local-inference"
    payload: str = ""
    deviceProfile: str = DEVICE_PROFILE


@app.get("/health")
def health() -> dict:
    """Liveness probe; also the warmup fallback for legacy stubs."""
    return {
        "status": "ok",
        "modelName": MODEL_NAME,
        "modelVersion": MODEL_VERSION,
        "deviceProfile": DEVICE_PROFILE,
    }


@app.post("/warmup")
def warmup(request: WarmupRequest) -> dict:
    """Warm the model before accepting work."""
    return {
        "status": "warmed",
        "modelName": request.modelName or MODEL_NAME,
        "modelVersion": request.modelVersion or MODEL_VERSION,
        "deviceProfile": request.deviceProfile or DEVICE_PROFILE,
    }


@app.post("/infer")
def infer(request: InferRequest, response: Response) -> dict:
    """Echo the payload as output. Send payload containing 'exhausted' to
    simulate GPU exhaustion (503 + exhausted:true) for safe-retry drills."""
    payload = request.payload or ""
    if "exhausted" in payload.lower():
        response.status_code = status.HTTP_503_SERVICE_UNAVAILABLE
        return {"error": "gpu exhausted", "exhausted": True}
    return {
        "output": payload,
        "confidence": 0.9,
        "modelHash": request.artifactHash or ARTIFACT_HASH,
        "device": request.deviceProfile or DEVICE_PROFILE,
    }
