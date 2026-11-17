"""Explicit live Discord attachment smoke test; sends only generated test data."""
import argparse
import asyncio
import configparser
import hashlib
import io
import json
import struct
import wave
import zlib

import discord


def fixtures():
    def chunk(kind, data):
        return struct.pack('!I', len(data)) + kind + data + struct.pack('!I', zlib.crc32(kind + data))
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!2I5B', 32, 32, 8, 2, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress((b'\0' + b'\x40\x90\xe0' * 32) * 32)) + chunk(b'IEND', b'')
    audio = io.BytesIO()
    with wave.open(audio, 'wb') as stream:
        stream.setparams((1, 2, 8000, 0, 'NONE', 'not compressed'))
        stream.writeframes(b'\0\0' * 2000)
    return {'mia-test.txt': b'MIA Discord attachment check\n', 'mia-test.png': png, 'mia-test.wav': audio.getvalue()}


async def main(args):
    config = configparser.ConfigParser()
    config.read(args.token_file, encoding='utf-8-sig')
    async with discord.Client(intents=discord.Intents.none()) as client:
        await client.login(config.get('discord', 'token').strip())
        channel = await client.fetch_channel(args.channel)
        data = fixtures()
        message = await channel.send('MIA 連線測試：以下為自動產生的文字、圖片及短靜音附件。請傳一張圖片或檔案，並 @MIA 說明希望如何處理，以驗證使用者端收件與回覆。',
                                     files=[discord.File(io.BytesIO(value), filename=name) for name, value in data.items()])
        fetched = await channel.fetch_message(message.id)
        for attachment in fetched.attachments:
            received = await attachment.read()
            assert hashlib.sha256(received).digest() == hashlib.sha256(data[attachment.filename]).digest()
        print(json.dumps({'message_id': str(message.id), 'channel_id': str(channel.id),
                          'verified_attachments': [a.filename for a in fetched.attachments]}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--token-file', required=True)
    parser.add_argument('--channel', type=int, required=True)
    asyncio.run(main(parser.parse_args()))
