"""Serve MIA's SQLite calendar over local MCP stdio."""

import asyncio
import json
from pathlib import Path
import sys

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp import types

# The launcher uses -I, so CPython does not add this script's directory.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from calendar_tools import calendar_handler, tool_schema


SCHEMA = tool_schema()
TOOL = types.Tool(name=SCHEMA["name"], description=SCHEMA["description"], inputSchema=SCHEMA["parameters"])


async def list_tools(_context, _params):
    return types.ListToolsResult(tools=[TOOL])


async def call_tool(_context, params):
    if params.name != TOOL.name:
        return types.CallToolResult(
            content=[types.TextContent(type="text", text="Unknown MIA calendar tool.")], isError=True)
    output = await asyncio.to_thread(calendar_handler, params.arguments or {})
    return types.CallToolResult(content=[types.TextContent(type="text", text=output)],
                                isError="error" in json.loads(output))


async def main():
    server = Server("mia-calendar", version="1.0",
                    on_list_tools=list_tools, on_call_tool=call_tool,
                    get_tool_input_schema=lambda name: TOOL.inputSchema if name == TOOL.name else None)
    async with stdio_server() as (incoming, outgoing):
        await server.run(incoming, outgoing, server.create_initialization_options())


if __name__ == "__main__":
    asyncio.run(main())
