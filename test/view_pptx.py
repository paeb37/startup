import json, base64

with open('love.json') as f:
    data = json.load(f)

with open('love.pptx', 'wb') as f:
    f.write(base64.b64decode(data['export']['pptxBase64']))

print('PPTX saved')