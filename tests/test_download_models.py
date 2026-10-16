"""Downloader integrity checks with tiny local files and fake HTTP responses."""

from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

# The download function is exercised through a fake requests transport, without
# requiring installation dependencies or opening any network connection.
with patch.dict(sys.modules, {"requests": Mock()}):
    import download_models as models


class Response:
    def __init__(self, payload, status=206, content_range="bytes 4-9/10"):
        self.payload = payload
        self.status_code = status
        self.headers = {"Content-Length": str(len(payload))}
        if content_range is not None:
            self.headers["Content-Range"] = content_range

    def __enter__(self): return self
    def __exit__(self, *args): pass
    def raise_for_status(self): pass
    def iter_content(self, size): yield self.payload


class ModelDownloadTests(unittest.TestCase):
    def test_wrong_resume_range_does_not_publish_corrupt_model(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent / "validation") as temporary:
            destination = Path(temporary) / "speaker.onnx"
            partial = destination.with_suffix(".onnx.partial")
            partial.write_bytes(b"0123")
            http = Mock()
            http.get.return_value = Response(b"012345", content_range="bytes 0-5/10")
            with patch.object(models, "HTTP", http), patch.object(models, "report"), patch.object(models.time, "sleep"):
                with self.assertRaisesRegex(OSError, "Content-Range"):
                    models.download("https://example.invalid/model", destination, 10)
            self.assertFalse(destination.exists())
            self.assertEqual(partial.read_bytes(), b"0123")

    def test_missing_or_inconsistent_resume_headers_preserve_partial(self):
        for value in (None, "bytes 4-9/11", "bytes 4-10/10", "bytes 4-3/10", "bytes 4-9/*"):
            with self.subTest(content_range=value):
                with tempfile.TemporaryDirectory(dir=Path(__file__).parent / "validation") as temporary:
                    destination = Path(temporary) / "speaker.onnx"
                    partial = destination.with_suffix(".onnx.partial")
                    partial.write_bytes(b"0123")
                    http = Mock()
                    http.get.return_value = Response(b"456789", content_range=value)
                    with patch.object(models, "HTTP", http), patch.object(models, "report"), patch.object(models.time, "sleep"):
                        with self.assertRaisesRegex(OSError, "Content-Range"):
                            models.download("https://example.invalid/model", destination, 10)
                    self.assertFalse(destination.exists())
                    self.assertEqual(partial.read_bytes(), b"0123")

    def test_correct_resume_or_full_response_restart_publishes_exact_bytes(self):
        for response in (Response(b"456789"), Response(b"0123456789", 200, None)):
            with self.subTest(status=response.status_code):
                with tempfile.TemporaryDirectory(dir=Path(__file__).parent / "validation") as temporary:
                    destination = Path(temporary) / "speaker.onnx"
                    destination.with_suffix(".onnx.partial").write_bytes(b"0123")
                    http = Mock()
                    http.get.return_value = response
                    with patch.object(models, "HTTP", http), patch.object(models, "report"), patch.object(models.time, "sleep"):
                        models.download("https://example.invalid/model", destination, 10)
                    self.assertEqual(destination.read_bytes(), b"0123456789")
                    self.assertEqual(http.get.call_args.kwargs["headers"],
                                     {"Range": "bytes=4-", "Accept-Encoding": "identity"})

    def test_truncated_range_retries_from_last_received_byte(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent / "validation") as temporary:
            destination = Path(temporary) / "speaker.onnx"
            destination.with_suffix(".onnx.partial").write_bytes(b"0123")
            http = Mock()
            http.get.side_effect = [Response(b"45"), Response(b"6789", content_range="bytes 6-9/10")]
            with patch.object(models, "HTTP", http), patch.object(models, "report"), patch.object(models.time, "sleep"):
                models.download("https://example.invalid/model", destination, 10)
            self.assertEqual(destination.read_bytes(), b"0123456789")
            self.assertEqual([call.kwargs["headers"]["Range"] for call in http.get.call_args_list],
                             ["bytes=4-", "bytes=6-"])

    def test_unknown_size_range_waits_for_advertised_total(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent / "validation") as temporary:
            destination = Path(temporary) / "speaker.onnx"
            destination.with_suffix(".onnx.partial").write_bytes(b"0123")
            http = Mock()
            http.get.side_effect = [Response(b"45", content_range="bytes 4-5/10"),
                                    Response(b"6789", content_range="bytes 6-9/10")]
            with patch.object(models, "HTTP", http), patch.object(models, "report"), patch.object(models.time, "sleep"):
                models.download("https://example.invalid/model", destination)
            self.assertEqual(destination.read_bytes(), b"0123456789")
            self.assertEqual(http.get.call_count, 2)

    def test_encoded_or_oversized_range_is_not_published(self):
        encoded = Response(b"456789")
        encoded.headers["Content-Encoding"] = "gzip"
        for response in (encoded, Response(b"456789x")):
            with self.subTest(headers=response.headers, length=len(response.payload)):
                with tempfile.TemporaryDirectory(dir=Path(__file__).parent / "validation") as temporary:
                    destination = Path(temporary) / "speaker.onnx"
                    partial = destination.with_suffix(".onnx.partial")
                    partial.write_bytes(b"0123")
                    http = Mock()
                    http.get.return_value = response
                    with patch.object(models, "HTTP", http), patch.object(models, "report"), patch.object(models.time, "sleep"):
                        with self.assertRaisesRegex(OSError, "Content-Range"):
                            models.download("https://example.invalid/model", destination, 10)
                    self.assertFalse(destination.exists())
                    self.assertEqual(partial.read_bytes(), b"0123")


if __name__ == "__main__":
    unittest.main()
