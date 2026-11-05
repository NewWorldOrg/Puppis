#!/bin/sh
set -eu

image="${1:?the image to start}"
readonly name=puppis-answers
readonly configuration=/etc/puppis/driver.json

fail() {
  echo "$*" >&2
  exit 1
}

budget="$(docker run --rm \
  --env "CARINA_DRIVER_CONFIG=${configuration}" \
  --volume "${PWD}/docker/driver.development.json:${configuration}:ro" \
  "${image}" --shutdown-budget)" \
  || fail "the image could not read the development configuration, so it is not one to hand out"

case "${budget}" in
  ''|*[!0-9]*) fail "the image answered --shutdown-budget with '${budget}', not a number of seconds" ;;
esac

echo "the image reads the development configuration and plans to wait ${budget}s for a recording when stopped."

docker run --detach --name "${name}" \
  --env "CARINA_DRIVER_CONFIG=${configuration}" \
  --volume "${PWD}/docker/driver.development.json:${configuration}:ro" \
  --tmpfs /run/carina:mode=0755 \
  --tmpfs /srv/recordings:mode=0755 \
  "${image}" >/dev/null
trap 'docker rm --force "${name}" >/dev/null' EXIT

probe=1

for _ in $(seq 1 30); do
  if docker exec "${name}" /opt/puppis/Puppis.Driver --probe >/dev/null 2>&1; then
    probe=0
    break
  fi

  sleep 1
done

if [ "${probe}" -ne 0 ]; then
  docker logs "${name}" >&2
  docker exec "${name}" /opt/puppis/Puppis.Driver --probe >&2 || true
  fail "the driver in the image did not answer its own --probe on the synthetic tuners within 30s, so it is not one to hand out"
fi

echo "the driver in the image answers --probe on the synthetic tuners."
