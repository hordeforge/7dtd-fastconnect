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
# The Unicode format characters (bidi overrides and embeddings, LRM/RLM, the
# zero-width joiners, the BOM) are invisible on a terminal but present in the
# bytes, so a reader sees a different string than grep does. They are dropped
# rather than blanked: each is invisible in every rendering, so a space left
# behind would only misalign the value.
#
# Every character is spelled as its UTF-8 bytes ($'\xNN', a raw byte in every
# locale) rather than as a code point ($'\uXXXX'). A code-point escape is
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

# One character per array element, so the walk never slices a table string:
# ${#str} and ${str:i:1} count characters under a UTF-8 locale and bytes
# under C, which is the locale dependence this file is written to avoid.
# A literal in this file would also be invisible to the next editor and
# unreviewable, so each entry carries its code point as a comment.
LOG_INVISIBLE_FORMAT_CHARS=(
	$'\xe2\x80\x8b' # U+200B zero-width space
	$'\xe2\x80\x8c' # U+200C zero-width non-joiner
	$'\xe2\x80\x8d' # U+200D zero-width joiner
	$'\xe2\x80\x8e' # U+200E left-to-right mark
	$'\xe2\x80\x8f' # U+200F right-to-left mark
	$'\xe2\x80\xaa' # U+202A left-to-right embedding
	$'\xe2\x80\xab' # U+202B right-to-left embedding
	$'\xe2\x80\xac' # U+202C pop directional formatting
	$'\xe2\x80\xad' # U+202D left-to-right override
	$'\xe2\x80\xae' # U+202E right-to-left override
	$'\xe2\x81\xa0' # U+2060 word joiner
	$'\xe2\x81\xa1' # U+2061 invisible function application
	$'\xe2\x81\xa2' # U+2062 invisible times
	$'\xe2\x81\xa3' # U+2063 invisible separator
	$'\xe2\x81\xa4' # U+2064 invisible plus
	$'\xe2\x81\xa6' # U+2066 left-to-right isolate
	$'\xe2\x81\xa7' # U+2067 right-to-left isolate
	$'\xe2\x81\xa8' # U+2068 first-strong isolate
	$'\xe2\x81\xa9' # U+2069 pop directional isolate
	$'\xef\xbb\xbf' # U+FEFF BOM / zero-width no-break space
)

# U+0001 to U+001F and U+007F. U+0000 is absent because a shell variable
# cannot hold it, so no value reaching this function carries one.
LOG_CONTROL_CHARS=(
	$'\x01' $'\x02' $'\x03' $'\x04' $'\x05' $'\x06' $'\x07' $'\x08'
	$'\x09' $'\x0a' $'\x0b' $'\x0c' $'\x0d' $'\x0e' $'\x0f' $'\x10'
	$'\x11' $'\x12' $'\x13' $'\x14' $'\x15' $'\x16' $'\x17' $'\x18'
	$'\x19' $'\x1a' $'\x1b' $'\x1c' $'\x1d' $'\x1e' $'\x1f' $'\x7f'
)

# U+0080 to U+009F (the C1 block) and U+2028/U+2029. The C1 characters are
# two-byte sequences, so they are generated rather than listed: 32 literal
# lines of the same shape would drift from the range they claim to cover.
LOG_C1_AND_SEPARATOR_CHARS=()
log_utf8_c1_and_separators() {
	# Every C1 character is U+0080..U+009F encoded as the two bytes C2 80..C2
	# 9F. The hex is formatted first and expanded by %b: printf's own format
	# string expands \x, not a variable holding the text "\xc2\x80", which it
	# would take literally.
	local i hex char
	for ((i = 0x80; i <= 0x9f; i++)); do
		printf -v hex '\\xc2\\x%02x' "$i"
		printf -v char '%b' "$hex"
		LOG_C1_AND_SEPARATOR_CHARS+=("$char")
	done
	LOG_C1_AND_SEPARATOR_CHARS+=(
		$'\xe2\x80\xa8' # U+2028 line separator
		$'\xe2\x80\xa9' # U+2029 paragraph separator
	)
}
log_utf8_c1_and_separators
readonly -a LOG_INVISIBLE_FORMAT_CHARS LOG_CONTROL_CHARS LOG_C1_AND_SEPARATOR_CHARS

fi

sanitize_log_text() {
	local text="$1" c
	# Each pass is a whole-value pattern substitution, so the value is walked
	# once per character in each table: 54 of them for a value that has
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
