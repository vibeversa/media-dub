#!/usr/bin/env bash

set -e

echo "==> Updating apt packages..."
sudo apt-get update

echo "==> Installing system dependencies..."
sudo apt-get install -y \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libpango-1.0-0 \
    libcairo2 \
    libasound2

echo "==> Installing Claude Code..."
curl -fsSL https://claude.ai/install.sh | bash

echo "==> Installing OpenCode..."
curl -fsSL https://opencode.ai/v2/install | bash

echo "==> Installing global npm packages..."
npm install -g \
    repomix \
    @playwright/mcp \
    chrome-devtools-mcp \
    @upstash/context7-mcp \
    @cyanheads/git-mcp-server \
    @modelcontextprotocol/server-filesystem \
    @modelcontextprotocol/server-memory \
    @modelcontextprotocol/server-sequential-thinking

echo "==> Installing Playwright test..."
npm install -D @playwright/test

echo "==> Installing Playwright browsers..."
npx playwright install --with-deps chromium

echo "==> Configuring OpenCode MCP servers..."

opencode mcp add git -- git-mcp-server
opencode mcp add context7 -- context7-mcp
opencode mcp add repomix -- repomix --mcp
opencode mcp add claude -- claude mcp serve
opencode mcp add playwright -- playwright-mcp
opencode mcp add memory -- mcp-server-memory
opencode mcp add filesystem -- mcp-server-filesystem .
opencode mcp add chrome-devtools -- chrome-devtools-mcp
opencode mcp add sequential-thinking -- mcp-server-sequential-thinking
opencode mcp add microsoft-learn --url https://learn.microsoft.com/api/mcp

echo "==> Configuring Git..."
git config --global user.name "Yahya Fazeli"
git config --global user.email "yahya.mehrabani@gmail.com"

echo ""
echo "========================================"
echo " Development environment is ready!"
echo "========================================"
