"""Chromium regression checks for the selection adapter. Uses a local fixture and an in-memory host port."""
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Thread
import unittest

from playwright.sync_api import sync_playwright

ADAPTER = Path(__file__).resolve().parents[1] / "extension" / "selection.js"
PAGE = """<!doctype html><style>p {width:600px;font:24px Georgia}</style>
<p id="line"><a id="word" href="/word">yield</a> to the current.</p>
<a id="other" href="/other">Another link</a><input id="input" value="form field"><input id="secret" type="password" value="hidden">
<script>window.presses = 0; addEventListener('mousedown', () => presses++);</script>"""
PORT = """
window.lookupMessages = [];
window.chrome ??= {};
chrome.runtime = {
  connect() {
    return {
      postMessage: message => lookupMessages.push(message),
      onMessage: { addListener: fn => {
        window.hostReady = ready => fn({ready});
        window.middleClick = middleClickEnabled => fn({middleClickEnabled});
        fn({ready:true});
      } },
      onDisconnect: { addListener() {} }, disconnect() {}
    };
  }
};
"""

class Fixture(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/html")
        self.end_headers()
        self.wfile.write(PAGE.encode())

    def log_message(self, *args):
        pass


class BrowserGestureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        executable = os.environ.get("PLAYWRIGHT_CHROMIUM_EXECUTABLE")
        cls.browser = cls.playwright.chromium.launch(executable_path=executable)
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), Fixture)
        Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        self.context = self.browser.new_context()
        self.context.add_init_script(PORT + ADAPTER.read_text(encoding="utf-8"))
        self.page = self.context.new_page()
        self.page.goto(f"http://127.0.0.1:{self.server.server_port}/")

    def tearDown(self):
        self.context.close()

    def select(self, selector):
        self.page.eval_on_selector(selector, """element => {
            const range = document.createRange();
            range.selectNodeContents(element);
            getSelection().removeAllRanges(); getSelection().addRange(range);
        }""")

    def click_link_normally(self, selector, suffix):
        before = len(self.context.pages)
        with self.context.expect_page(timeout=3000) as opened:
            self.page.locator(selector).click(button="middle")
        opened.value.wait_for_url("**" + suffix, timeout=4000)
        self.assertEqual(len(self.context.pages), before + 1)
        opened.value.close()
        self.assertFalse(self.page.evaluate("lookupMessages.some(m => m.type === 'lookup')"))

    def test_links_pass_through_with_no_selection_offline_or_lookup_disabled(self):
        self.click_link_normally("#word", "/word")
        self.select("#word")
        self.page.evaluate("hostReady(false)")
        self.click_link_normally("#word", "/word")
        self.page.evaluate("hostReady(true); middleClick(false)")
        self.select("#word")
        self.click_link_normally("#other", "/other")
        self.page.evaluate("middleClick(true)")
        self.select("#word")
        self.page.locator("#other").click(button="middle")
        self.assertEqual(self.page.evaluate("lookupMessages.at(-1).text"), "yield")
        self.assertEqual(len(self.context.pages), 1)

    def test_highlighted_link_sends_one_lookup_and_escape_dismisses(self):
        self.select("#word")
        self.page.locator("#word").click(button="middle")
        lookups = self.page.evaluate("lookupMessages.filter(m => m.type === 'lookup')")
        self.assertEqual([m["text"] for m in lookups], ["yield"])
        self.assertEqual(len(self.context.pages), 1)
        self.assertEqual(self.page.evaluate("presses"), 0)
        self.page.keyboard.press("Escape")
        self.assertEqual(self.page.evaluate("lookupMessages.at(-1).type"), "dismiss")

    def test_selection_wins_outside_its_glyphs_and_clearing_restores_click(self):
        self.select("#line")
        line = self.page.locator("#line").bounding_box()
        self.page.mouse.click(line["x"] + line["width"] - 2, line["y"] + 10, button="middle")
        self.assertEqual(self.page.evaluate("presses"), 0)
        self.assertEqual(self.page.evaluate("lookupMessages.at(-1).offsets"), [0, 0, 0, 0])
        self.assertEqual(self.page.evaluate("lookupMessages.at(-1).text"), "yield to the current.")
        self.page.evaluate("getSelection().removeAllRanges()")
        self.page.evaluate("lookupMessages.length = 0")
        self.click_link_normally("#word", "/word")

    def test_selected_input_wins_and_password_selections_are_excluded(self):
        self.page.eval_on_selector("#input", "el => { el.focus(); el.setSelectionRange(0,4); }")
        self.page.locator("#other").click(button="middle")
        self.assertEqual(self.page.evaluate("lookupMessages.at(-1).text"), "form")
        self.assertEqual(len(self.context.pages), 1)
        self.page.eval_on_selector("#secret", "el => { el.focus(); el.select(); }")
        self.page.evaluate("lookupMessages.length = 0")
        self.click_link_normally("#other", "/other")


if __name__ == "__main__":
    unittest.main()

