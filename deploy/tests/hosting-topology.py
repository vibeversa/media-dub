"""Network topology analyser for the hosting gate (Task 043, R4).

Run by `deploy/tests/hosting.test.sh`. Prints one `ok    ...` line per satisfied
assertion and one `FAIL: ...` line per violation, then exits 0 either way - the
EXIT CODE CARRIES NO VERDICT, because the caller must be able to distinguish
"checked, found nothing" from "crashed before checking anything". A non-zero exit
is reserved for the analyser itself failing, and the shell treats that as a
failure of the gate, not as a clean result.

The contract is therefore: exit 0 means the analyser ran to completion, and the
verdict is entirely in the printed lines. A missing PyYAML, a missing directory,
or a malformed manifest produces a message on stderr and a non-zero exit, which
the caller fails on. That is deliberate - the failure mode this file exists to
prevent is a topology check that reports success having parsed nothing.

String concatenation rather than f-strings throughout. Not a style choice: a
literal `{}` inside an f-string expression position is a SyntaxError before
Python 3.12, and this file has to run on whatever interpreter the host has. The
first version of this check used an f-string there, produced a traceback and no
assertions, and the caller counted zero `FAIL:` lines and reported a PASS.

A separate module rather than a heredoc inside the shell script, for the same
reason plus one more: a heredoc body is not a file, so nothing else can be
pointed at it, and a syntax error in it is only visible as a traceback the caller
has to notice is not a verdict.
"""

import os
import sys

try:
    import yaml
except ImportError:
    sys.stderr.write("PyYAML is required to parse the NetworkPolicies.\n")
    sys.exit(2)

if len(sys.argv) < 2:
    sys.stderr.write("usage: hosting-topology.py <deploy/k8s directory>\n")
    sys.exit(2)

base = sys.argv[1]
if not os.path.isdir(base):
    sys.stderr.write("not a directory: " + base + "\n")
    sys.exit(2)

problems = []
policies = []
# `os.walk`, not `os.listdir` (Task 043A). Task 043 recorded the flat listing as a
# gotcha to hand on; there is now a `deploy/k8s/frontend/` subdirectory, so the
# assumption is no longer a caveat but a live way for a policy placed beside its
# workload to be skipped by the checker that exists to read it. Silently. The
# verdict is in the printed lines, and a policy that was never read produces no
# line at all.
for root, _dirs, files in os.walk(base):
    for name in sorted(files):
        if not name.endswith(".yaml") and not name.endswith(".yml"):
            continue
        path = os.path.join(root, name)
        with open(path, encoding="utf-8") as handle:
            try:
                documents = list(yaml.safe_load_all(handle))
            except yaml.YAMLError as exc:
                sys.stderr.write(path + " is not valid YAML: " + str(exc) + "\n")
                sys.exit(2)
            for doc in documents:
                if isinstance(doc, dict) and doc.get("kind") == "NetworkPolicy":
                    policies.append((os.path.relpath(path, base).replace(os.sep, "/"), doc))

if not policies:
    print("FAIL: no NetworkPolicy in deploy/k8s at all")
    sys.exit(0)

def selector_labels(doc):
    sel = (doc.get("spec") or {}).get("podSelector") or {}
    if not sel:
        return None
    if "matchLabels" in sel:
        return sel["matchLabels"]
    if "matchExpressions" in sel:
        return {expr.get("key"): expr.get("values") for expr in sel["matchExpressions"]}
    return {}

# 1. A default deny that covers every pod and both directions. Without it,
#    every other policy here is an addition to "everything is allowed".
default_deny = None
for name, doc in policies:
    if (doc.get("spec") or {}).get("podSelector") == {} and \
       set(doc.get("spec", {}).get("policyTypes") or []) == {"Ingress", "Egress"}:
        default_deny = name
        break
if default_deny is None:
    problems.append(
        "no NetworkPolicy has an empty podSelector with policyTypes [Ingress, Egress]. Every allow below "
        "is an addition to 'everything is permitted' without one, which is the default-deny this task "
        "requires."
    )
else:
    # String concatenation, not an f-string: a literal `{}` inside an f-string
    # expression position is a SyntaxError on Python < 3.12, and this script must
    # run on whatever interpreter the host has. The failure it produces is a
    # traceback on stderr and NO assertions at all, which is the same silent
    # pass as a skipped check.
    print("ok    default-deny present: " + str(default_deny) + " (empty podSelector / Ingress+Egress)")

def selects(doc, label_key, label_value):
    labels = selector_labels(doc)
    if labels is None:
        return False
    return labels.get(label_key) == label_value

# 2. The API is reachable only from the ingress and the monitoring namespace.
#    A peer that is not a namespaceSelector is a wildcard, and a wildcard here is
#    "any pod in the cluster can call the API".
api_policies = [(n, d) for n, d in policies if selects(d, "app.kubernetes.io/component", "api")]
if not api_policies:
    problems.append("no NetworkPolicy selects the api component, so its ingress is unconstrained")
api_has_ingress = False
for name, doc in api_policies:
    spec = doc.get("spec") or {}
    for rule in spec.get("ingress") or []:
        api_has_ingress = True
        peers = rule.get("from")
        if not peers:
            problems.append(
                f"{name}: an api ingress rule has no `from`, which admits every source. On the API "
                "that is unauthenticated reachability from any pod in the cluster."
            )
        for peer in peers or []:
            if "namespaceSelector" not in peer:
                problems.append(
                    f"{name}: an api ingress peer is not a namespaceSelector: {sorted(peer)}. "
                    "A podSelector without a namespaceSelector matches pods in THIS namespace, "
                    "so the API would accept traffic from any workload deployed alongside it."
                )
            if "ipBlock" in peer:
                problems.append(
                    f"{name}: an api ingress peer is an ipBlock: {peer['ipBlock']}. An address is not "
                    "a peer: it admits traffic from outside the cluster, which the ingress-nginx "
                    "namespace already grants and without the ability to name it."
                )
    # The port must be the one the manifest actually serves. A policy that opens
    # 8080 while the container listens on 9090 denies everything, and the
    # symptom is a connection refused that reads as a pod problem.
    api_ports = sorted({p.get("port") for rule in (spec.get("ingress") or []) for p in (rule.get("ports") or [])})
    if api_ports and 8080 not in api_ports:
        problems.append(
            f"{name}: the api's ingress permits ports {api_ports}; api-deployment.yaml serves on 8080."
        )
if not api_has_ingress:
    problems.append("no NetworkPolicy gives the api an ingress rule at all")

# 3. The data stores accept ingress from nothing but the API and the migration
#    job. Checked by asserting that no policy named for a datastore GRANTS
#    ingress to it from a non-API, non-migration peer. The default-deny is what
#    actually protects them; this asserts nothing has quietly re-opened them.
DATASTORES = ("postgres", "rabbitmq", "redis", "minio")
for name, doc in policies:
    labels = selector_labels(doc) or {}
    targeted = [value for key, value in labels.items() if value in DATASTORES]
    if not targeted:
        continue
    for rule in (doc.get("spec") or {}).get("ingress") or []:
        for peer in rule.get("from") or []:
            peer_labels = (peer.get("podSelector") or {}).get("matchLabels") or {}
            # Task 047 added `backup`. It is in the set for a specific reason
            # rather than because the set was widened to make a finding go away:
            # the backup job's entire purpose is to read the database, and a
            # backup that cannot be dialled produces no archive - silently,
            # because the CronJob is hourly and nobody is watching it. The
            # counter-argument, that a data-reading peer is a data-exfiltration
            # peer, is answered by the other half rather than by this line: the
            # job has no ingress of its own, no broker or provider egress, a
            # non-root uid and a read-only root filesystem, and it is the only
            # workload in the namespace holding the object-storage write key.
            #
            # The migration job is still MISSING its datastore-ingress rule - the
            # same asymmetry, pre-existing. It is not added here: granting the
            # one workload that can DDL a new network path is a change with its
            # own review, and it is recorded as a gap in docs/backup.md rather
            # than being widened in inside a backup task. `datastores-allow-
            # backup` names the backup component alone, so this loop cannot be
            # satisfied by a rule that admits everything.
            allowed = {"api", "migration", "backup"}
            if not (set(peer_labels.values()) & allowed):
                problems.append(
                    f"{name}: policy for {targeted} admits {peer_labels or peer}, which is none of the api, "
                    "the migration job or the backup job. Only those three may reach a datastore over the network."
                )

# 4. The migration job reaches PostgreSQL and nothing else it does not need.
#    Least privilege (Task 043 security requirements).
migration_policies = [(n, d) for n, d in policies if selects(d, "app.kubernetes.io/component", "migration")]
if not migration_policies:
    problems.append(
        "no NetworkPolicy selects the migration component. It currently relies on the workers-allow "
        "egress rule, which grants it the broker and Redis as well as PostgreSQL; a migration that "
        "does not need a queue should not have one."
    )
else:
    for name, doc in migration_policies:
        # The job must not accept ingress at all. Nothing connects to a Job that
        # is applying a migration, and a Job that accepts a connection during one
        # can be probed for its state by anything in the namespace.
        if (doc.get("spec") or {}).get("ingress"):
            problems.append(
                f"{name}: the migration job's policy declares ingress rules. Nothing connects to a Job "
                "that is applying a migration."
            )
        for rule in (doc.get("spec") or {}).get("egress") or []:
            ports = sorted({p.get("port") for p in rule.get("ports") or []})
            if ports != [5432]:
                problems.append(
                    f"{name}: the migration job's egress permits ports {ports}; it needs PostgreSQL (5432) "
                    "and DNS (covered by allow-dns-egress) only. A migration with broker access can "
                    "publish, and a migration that publishes during a deploy delivers a message against "
                    "a schema mid-change."
                )
        # And it must reach PostgreSQL specifically, not merely "something".
        targets = [
            (peer.get("podSelector") or {}).get("matchLabels", {}).get("app")
            for rule in (doc.get("spec") or {}).get("egress") or []
            for peer in rule.get("to") or []
        ]
        if "postgres" not in targets:
            problems.append(
                f"{name}: the migration job's egress names no PostgreSQL target ({targets}). It must reach "
                "the database it is migrating; a rule that opens 5432 to something else is not that."
            )

# 5. The static origin is reachable only through the edge, and only by a peer that
#    names itself. Task 043 allowed exactly one namespace (the CDN's origin-facing
#    proxy). Task 043A added the TLS-terminating ingress, so there are two hops -
#    and the property that matters is that BOTH are named and NEITHER is a bare
#    selector, rather than "there is no ingress at all".
static_policies = [(n, d) for n, d in policies if selects(d, "app.kubernetes.io/component", "static")]
if not static_policies:
    problems.append(
        "no NetworkPolicy selects the static component. The static origin is behind a CDN and a TLS "
        "ingress, so without a policy it accepts traffic from every pod in the namespace."
    )
ALLOWED_STATIC_PEERS = {"cdn-edge", "ingress-nginx"}
for name, doc in static_policies:
    spec = doc.get("spec") or {}
    ingress_rules = spec.get("ingress") or []
    if not ingress_rules:
        problems.append(
            name + ": the static origin's policy declares no ingress rule at all. The default-deny is "
            "what protects it, and the default-deny is the absence of rules, not a rule."
        )
    for rule in ingress_rules:
        if not rule.get("from"):
            problems.append(
                name + ": a static ingress rule has no `from`, which admits every source. The static "
                "origin is the document everyone loads; an open rule is a direct route to it that "
                "bypasses the CDN's HTTPS redirect and security headers."
            )
        for peer in rule.get("from") or []:
            # A peer is constrained if it names WHO to accept from. A bare
            # `{}`, a bare `podSelector: {}`, or a podSelector with no labels all
            # match every pod in the namespace, which for the static origin means
            # every workload deployed alongside it.
            namespace = ((peer.get("namespaceSelector") or {}).get("matchLabels") or {}).get("name")
            pod_labels = (peer.get("podSelector") or {}).get("matchLabels")
            has_namespace = bool((peer.get("namespaceSelector") or {}).get("matchLabels"))
            has_pod = bool(pod_labels)
            if not has_namespace and not has_pod:
                problems.append(
                    name + ": a static ingress peer is unconstrained: " + str(peer) + ". It must name a "
                    "namespaceSelector (the CDN edge or the TLS ingress) so the origin accepts only "
                    "edge fetches."
                )
            if has_pod and not has_namespace:
                problems.append(
                    name + ": a static ingress peer uses a bare podSelector (" + str(pod_labels) + "). A "
                    "podSelector with no namespaceSelector matches pods in THIS namespace, so the origin "
                    "would accept traffic from the API, the workers, and anything an attacker can schedule."
                )
            if namespace is not None and namespace not in ALLOWED_STATIC_PEERS:
                problems.append(
                    name + ": the static origin accepts traffic from the '" + str(namespace) + "' namespace. "
                    "Only " + ", ".join(sorted(ALLOWED_STATIC_PEERS)) + " are hops in front of the document; "
                    "any other peer is a route to it that carries no edge policy."
                )
            if "ipBlock" in peer:
                problems.append(
                    name + ": a static ingress peer is an ipBlock: " + str(peer["ipBlock"]) + ". An address "
                    "is not a peer: it admits traffic from outside the cluster with nothing naming who it is."
                )
    # And the edge must be reachable on the port the manifest actually serves.
    ports = sorted({p.get("port") for rule in (spec.get("ingress") or []) for p in (rule.get("ports") or [])})
    if ports and 8080 not in ports:
        problems.append(
            name + ": the static origin's ingress permits ports " + str(ports) + "; the image serves on 8080 "
            "(deploy/k8s/frontend/deployment.yaml, the unprivileged nginx image cannot bind 80)."
        )

# 6. Workers accept no ingress at all.
worker_policies = [
    (n, d) for n, d in policies
    if (selector_labels(d) or {}).get("app.kubernetes.io/component") is None
    and (d.get("spec") or {}).get("policyTypes") == ["Egress"]
]
for name, doc in worker_policies:
    if doc.get("spec", {}).get("ingress"):
        problems.append(f"{name}: a worker policy declares ingress rules; workers accept none")

for problem in problems:
    print("FAIL: " + problem)
if not problems:
    print("ok    " + str(len(policies)) + " NetworkPolicy/policies checked")
