#!/usr/bin/env bash
# Flatten line-breaking and invisible-format characters in values written to
# lifecycle logs.
#
# Shell-side twin of LogText.SanitizeForLog: 7DTD_CONNECT / CYCLE are
# attacker-shapable (a clicked steam://run URL chooses -connect= text), and
# join tooling greps these logs for fixed markers ("result=", "===" cycle
# headers); an embedded line break could forge those markers without ever
# connecting. C0 controls, DEL, the C1 block, and U+2028/U+2029 become spaces
# so one value stays one line. That is the same set the C# twin flattens.
# Every replacement is bash pattern substitution, which works in characters;
# tr would have to match the C1 range and the two separators byte-wise, which
# mangles every other multi-byte character. That also keeps the function free
# of external binaries, so it still works in the stripped-PATH runs the
# degradation tests use (a helper with only bash on PATH must not lose the
# flattening that keeps its warning one line).
#
# The Unicode format characters (general category Cf: the bidi controls, the
# zero-width joiners, the BOM, the Egyptian hieroglyph format controls, the
# tag block) are invisible on a terminal but present in the bytes, so a reader
# sees a different string than grep does. They are dropped rather than blanked:
# each is invisible in every rendering, so a space left behind would only
# misalign the value.
#
# The set is spelled out as code points rather than queried from a category
# table, because the table belongs to the runtime and the two runtimes this
# file must match do not agree on it: the mod runs on the Mono that ships with
# the game, whose Unicode database predates the Egyptian format controls, the
# shorthand format controls and the whole U+E0001 tag block, so a category
# query there passes every Trojan Source and tag-spoofing character straight
# through. The list below is the whole of Cf as Unicode 15.1 defines it, which
# is what a current table returns.
#
# Every character is built from its UTF-8 bytes ($'\xNN', a raw byte in every
# locale) rather than from a code-point escape ($'\uXXXX'), which is
# decoded by the shell according to the locale in effect when the file is
# sourced: under LC_ALL=C or POSIX, which a container, a systemd unit or a
# bare Proton prefix hands these scripts as readily as en_US.UTF-8,
# $'\u200b' stays the six literal characters \u200b, ${#table} then counts
# those bytes, and the walk below substitutes one ASCII character of the
# escape text at a time. A value carrying a single é came out as caf<0xc3>
# followed by a space, and a real U+2028 in the value survived unflattened:
# the helper corrupted the text it exists to clean and dropped the protection
# at the same time. Byte escapes are the encoding, not an interpretation of
# it, so the tables and the walk mean the same thing in every locale.
#
# Source this file; do not execute it.

# Guarded because more than one sourced library pulls this file in (the
# scripts source it directly, config_validate.sh sources it for env_bool):
# a second `readonly` assignment to the same name in one shell is an error,
# which under `set -e` would kill a script that did nothing wrong.
if [[ -z "${LOG_INVISIBLE_FORMAT_CHARS[0]:-}" ]]; then

# Whitespace-separated code points and inclusive lo-hi ranges, the whole of
# Unicode general category Cf. Hex code points rather than literals, because a
# literal here would be invisible to the next editor and unreviewable.
LOG_INVISIBLE_FORMAT_SPEC='00ad 0600-0605 061c 06dd 070f 0890-0891 08e2 180e
	200b-200f 202a-202e 2060-2064 2066-206f feff fff9-fffb 110bd 110cd
	13430-1343f 1bca0-1bca3 1d173-1d17a e0001 e0020-e007f'

# Sets REPLY to the UTF-8 encoding of code point $1, assembled from \xNN byte
# escapes so the result is the same bytes in every locale. The hex is
# formatted first and expanded by %b: printf's own format string expands \x,
# not a variable holding the text "\xc2\x80", which it would take literally.
log_utf8_char() {
	local cp=$1 hex
	if ((cp < 0x80)); then
		printf -v hex '\\x%02x' "$cp"
	elif ((cp < 0x800)); then
		printf -v hex '\\x%02x\\x%02x' $((0xc0 | cp >> 6)) $((0x80 | (cp & 0x3f)))
	elif ((cp < 0x10000)); then
		printf -v hex '\\x%02x\\x%02x\\x%02x' $((0xe0 | cp >> 12)) \
			$((0x80 | (cp >> 6 & 0x3f))) $((0x80 | (cp & 0x3f)))
	else
		printf -v hex '\\x%02x\\x%02x\\x%02x\\x%02x' $((0xf0 | cp >> 18)) \
			$((0x80 | (cp >> 12 & 0x3f))) $((0x80 | (cp >> 6 & 0x3f))) \
			$((0x80 | (cp & 0x3f)))
	fi
	printf -v REPLY '%b' "$hex"
}

# One character per array element, so the walk never slices a table string:
# ${#str} and ${str:i:1} count characters under a UTF-8 locale and bytes
# under C, which is the locale dependence this file is written to avoid.
LOG_INVISIBLE_FORMAT_CHARS=()
log_expand_format_spec() {
	local item lo hi i
	for item in $LOG_INVISIBLE_FORMAT_SPEC; do
		lo=$((16#${item%%-*}))
		hi=$((16#${item##*-}))
		for ((i = lo; i <= hi; i++)); do
			log_utf8_char "$i"
			LOG_INVISIBLE_FORMAT_CHARS+=("$REPLY")
		done
	done
}
log_expand_format_spec

# U+0001 to U+001F and U+007F. U+0000 is absent because a shell variable
# cannot hold it, so no value reaching this function carries one.
LOG_CONTROL_CHARS=(
	$'\x01' $'\x02' $'\x03' $'\x04' $'\x05' $'\x06' $'\x07' $'\x08'
	$'\x09' $'\x0a' $'\x0b' $'\x0c' $'\x0d' $'\x0e' $'\x0f' $'\x10'
	$'\x11' $'\x12' $'\x13' $'\x14' $'\x15' $'\x16' $'\x17' $'\x18'
	$'\x19' $'\x1a' $'\x1b' $'\x1c' $'\x1d' $'\x1e' $'\x1f' $'\x7f'
)

# U+0080 to U+009F (the C1 block) and U+2028/U+2029, generated rather than
# listed: 32 literal lines of the same shape would drift from the range they
# claim to cover.
LOG_C1_AND_SEPARATOR_CHARS=()
log_utf8_c1_and_separators() {
	local i
	for ((i = 0x80; i <= 0x9f; i++)); do
		log_utf8_char "$i"
		LOG_C1_AND_SEPARATOR_CHARS+=("$REPLY")
	done
	LOG_C1_AND_SEPARATOR_CHARS+=(
		$'\xe2\x80\xa8' # U+2028 line separator
		$'\xe2\x80\xa9' # U+2029 paragraph separator
	)
}
log_utf8_c1_and_separators
readonly LOG_INVISIBLE_FORMAT_SPEC
readonly -a LOG_INVISIBLE_FORMAT_CHARS LOG_CONTROL_CHARS LOG_C1_AND_SEPARATOR_CHARS

fi

sanitize_log_text() {
	local text="$1" c
	# Each pass is a whole-value pattern substitution, so the value is walked
	# once per character in each table: 236 of them for a value that has
	# nothing to flatten, which is the common case (a sanitized log line is
	# mostly ordinary text). A presence test first skips the substitution
	# when the character is absent, and it is the same match the
	# substitution itself makes, so the guard never changes what the pass
	# would have written, only whether the walk is worth doing.
	for c in "${LOG_INVISIBLE_FORMAT_CHARS[@]}"; do
		[[ $text == *"$c"* ]] && text="${text//"$c"/}"
	done
	for c in "${LOG_CONTROL_CHARS[@]}" "${LOG_C1_AND_SEPARATOR_CHARS[@]}"; do
		[[ $text == *"$c"* ]] && text="${text//"$c"/ }"
	done
	printf '%s' "$text"
}
