#!/usr/bin/env python3
"""A release's offline image archives made from an image a test pushed to a registry of its own on loopback (#127): the
image's index and every blob of each of its platforms, fetched over plain HTTP into an OCI archive, as
docker buildx build --output type=oci writes one, each blob as the registry holds it so the index keeps its digest; then
split into the archives by build/release-assets.py, as the release workflow splits the archive it pushed.

  tests/releasing/offline-archives-from-registry.py <host:port> <repository> <index digest> <image> <name> <version> <folder>

<repository> is where the registry holds the image (rig-build/roof-controller), <image> the archive's file name's
(roof-controller), and <name> the repository the archives name the image as, and release.json does
(127.0.0.1:15001/hualapaivalley/roof-controller). It prints the archives' entries for release.json, as JSON. Used by
tests/installer/rig-scenario.sh and tests/emulator/deploy-scenarios.sh.
"""

import importlib.util
import io
import json
import pathlib
import sys
import tarfile
import tempfile
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]


def main(argv):
    host, repository, digest, image, name, version, folder = argv
    spec = importlib.util.spec_from_file_location("release_assets", ROOT / "build" / "release-assets.py")
    assets = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(assets)
    accept = ", ".join(sorted(assets.INDEX_TYPES | assets.MANIFEST_TYPES))

    def get(kind, reference):
        request = urllib.request.Request(f"http://{host}/v2/{repository}/{kind}/{reference}", headers={"Accept": accept})
        with urllib.request.urlopen(request, timeout=300) as response:
            return response.read(), response.headers.get("Content-Type", "").split(";")[0].strip()

    index, index_type = get("manifests", digest)
    blobs = {digest: index}
    for entry in json.loads(index)["manifests"]:
        data, _ = get("manifests", entry["digest"])
        blobs[entry["digest"]] = data
        manifest = json.loads(data)
        # An image with no layers (FROM scratch, with labels alone) has them null.
        for descriptor in [manifest["config"], *(manifest.get("layers") or [])]:
            blobs[descriptor["digest"]] = get("blobs", descriptor["digest"])[0]

    with tempfile.TemporaryDirectory() as work:
        source = pathlib.Path(work) / "source.oci.tar"
        with tarfile.open(source, "w") as tar:
            def add(path, data):
                info = tarfile.TarInfo(path)
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))

            add("oci-layout", b'{"imageLayoutVersion":"1.0.0"}')
            add("index.json", json.dumps({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json",
                                          "manifests": [{"mediaType": index_type, "digest": digest, "size": len(index)}]}).encode())
            for blob, data in blobs.items():
                add(f"blobs/sha256/{blob[7:]}", data)
        archives = assets.offline_archives(source, image, name, version, digest, pathlib.Path(folder), 0)
    print(json.dumps(archives))
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 8:
        print(__doc__.split("\n\n")[1], file=sys.stderr)
        sys.exit(2)
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception as error:  # A test's helper: whatever failed, say so and fail.
        print(f"offline-archives-from-registry.py: {error}", file=sys.stderr)
        sys.exit(1)
