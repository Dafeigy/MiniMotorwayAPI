import requests
url = "localhost:9000"
req =requests.get("http://127.0.0.1:9000/tiles")
print(req.text)