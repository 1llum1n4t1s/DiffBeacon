"""Generate inputs only. Results must come from the original native Glob."""
import json
import pathlib

cases = []
def add(name, subject, pattern, sensitive=True):
    cases.append(dict(id=name, subject=subject, pattern=pattern, caseSensitive=sensitive))

for name, subject, pattern in [
    ('whole', 'abc', 'abc'), ('substring', 'xabcx', 'abc'),
    ('prefix', 'abcx', 'abc'), ('suffix', 'xabc', 'abc'),
    ('empty-both', '', ''), ('empty-subject', '', 'a'), ('empty-pattern', 'a', ''),
    ('empty-star', '', '*'), ('empty-many-stars', '', '***'), ('empty-question', '', '?'),
    ('star-zero', 'ab', 'a*b'), ('star-many', 'axyzb', 'a*b'),
    ('star-backtrack', 'abab', '*ab'), ('star-multiple', 'abcde', 'a*c*e'),
    ('question-bmp', '猫', '?'), ('question-astral', '😀', '?'),
    ('question-combining', 'e\u0301', '?'), ('two-combining', 'e\u0301', '??'),
    ('star-combining', 'e\u0301', '*'),
    ('set-yes', 'b', '[abc]'), ('set-no', 'z', '[abc]'),
    ('negset-yes', 'z', '[!abc]'), ('negset-no', 'b', '[!abc]'),
    ('range-yes', 'd', '[a-z]'), ('range-no', 'D', '[a-z]'), ('range-reverse', 'd', '[z-a]'),
    ('set-bmp', '猫', '[犬猫]'), ('set-astral', '😀', '[😀]'), ('range-astral', '😁', '[😀-😂]'),
    ('regex-punctuation', '^a.b+$', '^a.b+$'), ('regex-parens', '(a|b){2}', '(a|b){2}'),
    ('regex-dot-no', 'axb', 'a.b'), ('newline-star', 'a\nb', 'a*b'),
    ('newline-question', '\n', '?'), ('newline-literal', 'a\nb', 'a\nb'),
    ('crlf-star', 'a\r\nb', 'a*b'), ('slash-star', 'a/b', '*'), ('slash-question', '/', '?'),
    ('dot-star', '.abc', '*'), ('dot-question', '.', '?'),
    ('invalid-a', 'a', '[a'), ('invalid-b', 'b', '[a'),
    ('invalid-neg-b', 'b', '[!a'), ('invalid-neg-a', 'a', '[!a'),
    ('invalid-empty', '', '['), ('invalid-open', 'a', '['), ('invalid-bang', 'a', '[!'),
    ('invalid-range', 'a', '[a-'), ('invalid-nonmatch-extra', 'bc', '[a'),
    ('terminal-backslash-empty', '', '\\'), ('terminal-backslash-subject', 'a', '\\'),
    ('terminal-backslash-after-empty', 'a', 'a\\'),
    ('terminal-backslash-after-nonempty', 'ab', 'a\\'), ('terminal-backslash-prefix-miss', 'b', 'a\\'),
    ('escaped-star', '*', '\\*'), ('escaped-question', '?', '\\?'),
    ('escaped-open', '[', '\\['), ('escaped-close', ']', '\\]'),
    ('escaped-hyphen', '-', '[\\-]'), ('escaped-bang', '!', '[\\!]'),
    ('escaped-backslash', '\\', '\\\\'), ('set-empty', 'a', '[]'), ('set-neg-empty', 'a', '[!]'),
    ('star-invalid-empty', '', '*['), ('star-invalid-nonempty', 'a', '*['),
]: add(name, subject, pattern)

for name, subject, pattern in [
    ('ascii', 'ABC', 'abc'), ('latin', 'ÄÉ', 'äé'), ('greek', 'Σ', 'σ'),
    ('final-sigma', 'ς', 'σ'), ('cyrillic', 'Я', 'я'), ('deseret', '\U00010400', '\U00010428'),
    ('dotted-I', 'İ', 'i'), ('dotless-I', 'ı', 'I'), ('sharp-s', 'ß', 'SS'),
    ('capital-sharp', 'ẞ', 'ß'), ('kelvin', 'K', 'k'), ('long-s', 'ſ', 's'),
    ('titlecase', 'ǅ', 'ǆ'), ('nfc-nfd', 'é', 'e\u0301'), ('range-ci', 'D', '[a-z]'),
    ('range-bmp-ci', 'Ö', '[ä-ü]'), ('set-ci', 'Σ', '[σ]'),
]:
    add(name+'-ci', subject, pattern, False)
    add(name+'-cs', subject, pattern, True)

if __name__ == '__main__':
    pathlib.Path(__file__).with_name('cases.json').write_text(
        json.dumps(cases, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
