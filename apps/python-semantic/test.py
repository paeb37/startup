from supabase_client import fetch_deck_json, load_supabase_config
from pprint import pprint

config = load_supabase_config()
# print(config)

deck = fetch_deck_json("1c750771-c5d8-409e-bc5b-1c4db5719905")
# print(deck.keys())

# print(deck["file"], deck["slideCount"])

pprint(deck)