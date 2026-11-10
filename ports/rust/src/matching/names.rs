//! A name reduced to what decides a match (spec section 5.3): read as a company and as a person.

use std::collections::BTreeSet;

use super::data::MatcherData;
use super::text::{RawToken, Token, TokenRef};

#[derive(Debug, Default)]
pub struct NameForm {
    pub core: Vec<TokenRef>,
    pub generic: Vec<String>,
    pub legal_classes: Vec<String>,
    pub initials: Vec<TokenRef>,
    pub legal_only: bool,
    pub is_person: bool,
}

impl NameForm {
    pub fn is_empty(&self) -> bool {
        self.core.is_empty()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    Core,
    Legal,
    AmbiguousLegal,
    Generic,
    NearGeneric,
    Stop,
    Honorific,
}

const SOLE_TRADER_CLASS: &str = "SOLE";

struct Classified {
    token: TokenRef,
    class: Kind,
    legal_class: String,
    generic: String,
}

pub struct NameParser<'a> {
    pub data: &'a MatcherData,
}

impl NameParser<'_> {
    /// A name read as a company: legal forms and generic words stripped from the ends.
    pub fn entity(&self, tokens: &[RawToken]) -> NameForm {
        let mut items: Vec<Classified> = Vec::new();
        for raw in tokens {
            let (class, legal_class, generic) = self.classify(&raw.token);
            if class != Kind::Stop {
                items.push(Classified { token: raw.token.clone(), class, legal_class, generic });
            }
        }

        let has_core = items.iter().any(|item| matches!(item.class, Kind::Core | Kind::Honorific | Kind::AmbiguousLegal));
        for item in items.iter_mut() {
            if item.class == Kind::NearGeneric {
                item.class = if has_core { Kind::Generic } else { Kind::Core };
            }
        }

        if !items.is_empty() && items.iter().all(|item| item.class == Kind::Legal) {
            let mut classes: Vec<String> = Vec::new();
            for item in &items {
                if !classes.contains(&item.legal_class) {
                    classes.push(item.legal_class.clone());
                }
            }
            classes.sort();
            return NameForm { legal_classes: classes, legal_only: true, ..Default::default() };
        }

        let mut legal_classes: BTreeSet<String> = BTreeSet::new();
        let has_core_besides = |items: &[Classified], index: usize| {
            items.iter().enumerate().any(|(i, item)| i != index && matches!(item.class, Kind::Core | Kind::Honorific))
        };
        let has_any_besides =
            |items: &[Classified], index: usize| items.iter().enumerate().any(|(i, item)| i != index && item.class != Kind::Legal);
        let mut changed = true;
        while changed && items.len() > 1 {
            changed = false;
            for index in [items.len() - 1, 0] {
                let item = &items[index];
                if (item.class == Kind::Legal && has_any_besides(&items, index))
                    || (item.class == Kind::AmbiguousLegal && has_core_besides(&items, index))
                {
                    legal_classes.insert(item.legal_class.clone());
                    items.remove(index);
                    changed = true;
                    break;
                }
            }
        }
        items.retain(|item| {
            if item.class == Kind::Legal {
                legal_classes.insert(item.legal_class.clone());
                false
            } else {
                true
            }
        });
        // Sorted ordinally, as the reference's OrderBy(StringComparer.Ordinal): BTreeSet's byte order.
        let classes: Vec<String> = legal_classes.into_iter().collect();

        let mut core: Vec<TokenRef> = Vec::new();
        let mut generic_items: Vec<&Classified> = Vec::new();
        for item in &items {
            match item.class {
                Kind::Core | Kind::AmbiguousLegal | Kind::Honorific => core.push(item.token.clone()),
                Kind::Generic => generic_items.push(item),
                _ => {}
            }
        }
        if core.is_empty() {
            let core = generic_items.iter().map(|item| item.token.clone()).collect();
            return NameForm { core, legal_classes: classes, ..Default::default() };
        }
        let mut generic: Vec<String> = Vec::new();
        for item in generic_items {
            if !generic.contains(&item.generic) {
                generic.push(item.generic.clone());
            }
        }
        generic.sort();
        NameForm { core, generic, legal_classes: classes, ..Default::default() }
    }

    /// A name read as a person's: honorifics dropped, single letters kept as initials.
    pub fn person(&self, tokens: &[RawToken]) -> NameForm {
        let mut form = NameForm { is_person: true, ..Default::default() };
        for raw in tokens {
            if let Some(initials) = &raw.initials {
                form.initials.extend(initials.iter().cloned());
                continue;
            }
            let (kind, legal_class, _) = self.classify(&raw.token);
            if kind == Kind::Stop
                || kind == Kind::Honorific
                || (matches!(kind, Kind::Legal | Kind::AmbiguousLegal) && legal_class == SOLE_TRADER_CLASS)
            {
                continue;
            }
            form.core.push(raw.token.clone());
        }
        form
    }

    fn classify(&self, token: &Token) -> (Kind, String, String) {
        if token.is_number {
            return (Kind::Core, String::new(), String::new());
        }
        let keys = || std::iter::once(&token.text).chain(token.forms.iter());
        for k in keys() {
            if let Some(legal) = self.data.legal_forms.get(k) {
                let kind = if legal.ambiguous { Kind::AmbiguousLegal } else { Kind::Legal };
                return (kind, legal.class.clone(), String::new());
            }
        }
        for k in keys() {
            if let Some(canonical) = self.data.generic.get(k) {
                return (Kind::Generic, String::new(), canonical.clone());
            }
        }
        for form in &token.forms {
            if let Some(near) = self.data.near_generic(form) {
                return (Kind::NearGeneric, String::new(), near.to_string());
            }
        }
        if keys().any(|k| self.data.stop_words.contains(k)) {
            return (Kind::Stop, String::new(), String::new());
        }
        if keys().any(|k| self.data.honorifics.contains(k)) {
            return (Kind::Honorific, String::new(), String::new());
        }
        (Kind::Core, String::new(), String::new())
    }
}
