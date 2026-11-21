"""Read a PNG from stdin and return recognized text as UTF-8."""

import sys
from contextlib import redirect_stdout
from pathlib import Path


def recognize(png: bytes) -> str:
    with redirect_stdout(sys.stderr):
        import rapidocr

        models = Path(rapidocr.__file__).parent / "models"
        detector = models / "PP-OCRv6_det_small.onnx"
        recognizer = models / "PP-OCRv6_rec_small.onnx"
        if not detector.is_file() or not recognizer.is_file():
            raise FileNotFoundError("Bundled PP-OCRv6 models are missing")

        engine = rapidocr.RapidOCR(
            params={
                "Det.model_path": str(detector),
                "Rec.model_path": str(recognizer),
                "EngineConfig.onnxruntime.intra_op_num_threads": 4,
                "EngineConfig.onnxruntime.inter_op_num_threads": 1,
            }
        )
        result = engine(png, use_cls=False)

    lines = result.txts if result.txts is not None else ()
    return "\n".join(line.strip() for line in lines if line.strip())


if __name__ == "__main__":
    image = sys.stdin.buffer.read()
    if not image:
        raise ValueError("Missing screenshot data")
    sys.stdout.buffer.write(recognize(image).encode("utf-8"))
