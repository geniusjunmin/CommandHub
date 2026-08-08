#!/usr/bin/env bash
set -Eeuo pipefail

results_directory="${1:-TestResults}"
mapfile -t reports < <(find "$results_directory" -type f -name coverage.cobertura.xml -print)
if (( ${#reports[@]} == 0 )); then
  echo "No Cobertura coverage reports found under $results_directory." >&2
  exit 1
fi

# Merge line/branch hits from every test project. Contracts.cs and Options.cs only
# contain DTO/configuration declarations; the Application gate measures executable
# business logic. Generated migration artifacts are excluded by coverage.runsettings.
awk '
function attribute(line, name,    expression, value) {
  expression = name "=\"[^\"]+\""
  if (!match(line, expression)) return ""
  value = substr(line, RSTART + length(name) + 2, RLENGTH - length(name) - 3)
  return value
}
function percentage(hit, total) { return total == 0 ? 0 : (100 * hit / total) }
/<class / {
  filename = attribute($0, "filename")
  classname = attribute($0, "name")
}
/<line / {
  number = attribute($0, "number")
  hits = attribute($0, "hits") + 0
  key = filename ":" number

  if (filename ~ /^CommandHub.Application\// &&
      filename !~ /CommandHub.Application\/(Contracts|Options)\.cs$/) {
    application_lines[key] = 1
    if (hits > 0) application_hits[key] = 1
  }

  if (filename ~ /^CommandHub.Infrastructure\/Execution\//) {
    infrastructure_lines[key] = 1
    if (hits > 0) infrastructure_hits[key] = 1
  }

  state_machine = classname ~ /^CommandHub.Infrastructure.Execution.LiveExecutionRegistry/ ||
    (classname ~ /^CommandHub.Infrastructure.Execution.SshCommandExecutionProvider\// &&
     classname ~ /(StartAsync|PumpAsync|CancelAsync)/)
  if (state_machine && $0 ~ /branch=\"True\"/ &&
      match($0, /condition-coverage=\"[0-9.]+% \([0-9]+\/[0-9]+\)\"/)) {
    coverage = substr($0, RSTART, RLENGTH)
    sub(/^.*\(/, "", coverage)
    sub(/\).*$/, "", coverage)
    split(coverage, values, "/")
    if ((values[1] + 0) > state_branch_hits[key]) state_branch_hits[key] = values[1] + 0
    if ((values[2] + 0) > state_branch_totals[key]) state_branch_totals[key] = values[2] + 0
  }
}
END {
  for (key in application_lines) application_total++
  for (key in application_hits) application_hit++
  for (key in infrastructure_lines) infrastructure_total++
  for (key in infrastructure_hits) infrastructure_hit++
  for (key in state_branch_totals) {
    state_total += state_branch_totals[key]
    state_hit += state_branch_hits[key]
  }

  application_rate = percentage(application_hit, application_total)
  infrastructure_rate = percentage(infrastructure_hit, infrastructure_total)
  state_rate = percentage(state_hit, state_total)
  printf "Application business logic line coverage: %d/%d (%.2f%%; required 75%%)\n", application_hit, application_total, application_rate
  printf "Infrastructure execution line coverage: %d/%d (%.2f%%; required 65%%)\n", infrastructure_hit, infrastructure_total, infrastructure_rate
  printf "Cancellation/SSH state-machine branch coverage: %d/%d (%.2f%%; required 80%%)\n", state_hit, state_total, state_rate

  failed = 0
  if (application_rate + 0.0001 < 75) failed = 1
  if (infrastructure_rate + 0.0001 < 65) failed = 1
  if (state_rate + 0.0001 < 80) failed = 1
  exit failed
}
' "${reports[@]}"
