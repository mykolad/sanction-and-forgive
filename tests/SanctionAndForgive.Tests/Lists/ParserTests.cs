using System.Text;
using SanctionAndForgive.Lists;
using SanctionAndForgive.Lists.Parsers;

namespace SanctionAndForgive.Tests.Lists;

// Each parser on a small fixture in its feed's shape. Names and records are fictional.
public class ParserTests
{
    private static List<ParsedSubject> Parse(IListParser parser, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return parser.Parse(stream, CancellationToken.None).ToList();
    }

    [Fact]
    public void Eu_ReadsNamesAddressesAndWeakAliases()
    {
        var subjects = Parse(new EuParser(), """
            <?xml version="1.0" encoding="UTF-8"?>
            <export xmlns="http://eu.europa.ec/fpi/fsd/export" generationDate="2026-11-05T10:00:00+01:00">
              <sanctionEntity designationDate="2022-03-15" logicalId="9001">
                <remark>Fictional.</remark>
                <regulation programme="RUS"><publicationUrl>https://eur-lex.europa.eu/x</publicationUrl></regulation>
                <subjectType code="enterprise" classificationCode="E" />
                <nameAlias wholeName="Romashka LLC" strong="true" />
                <nameAlias wholeName="ООО Ромашка" strong="true" />
                <nameAlias wholeName="Romashka" strong="false" />
                <address street="Lenina 5" city="Moscow" countryIso2Code="RU" countryDescription="RUSSIAN FEDERATION" />
              </sanctionEntity>
              <sanctionEntity logicalId="9002">
                <subjectType code="person" classificationCode="P" />
                <nameAlias firstName="Taras" lastName="Shevchenko" wholeName="" strong="true" />
              </sanctionEntity>
            </export>
            """);

        Assert.Equal(2, subjects.Count);
        var company = subjects[0];
        Assert.Equal(("9001", SubjectType.Entity, "Romashka LLC"), (company.ExternalId, company.Type, company.PrimaryName));
        Assert.Equal("RUS", company.Programmes);
        Assert.Equal(new DateOnly(2022, 3, 15), company.ListedOn);
        Assert.Equal("https://eur-lex.europa.eu/x", company.SourceUrl);
        Assert.Contains(company.Names, name => name is { FullName: "ООО Ромашка", Script: NameScript.Cyrillic, IsWeak: false });
        Assert.Contains(company.Names, name => name is { FullName: "Romashka", IsWeak: true });
        Assert.Equal(new ParsedAddress("Lenina 5, Moscow, RUSSIAN FEDERATION", "RU"), Assert.Single(company.Addresses));
        Assert.Equal(("Taras Shevchenko", SubjectType.Individual), (subjects[1].PrimaryName, subjects[1].Type));
    }

    [Fact]
    public void Ofac_JoinsFirstAndLastName_KeepsScripts_SkipsVessels()
    {
        var subjects = Parse(new OfacParser("ofac-sdn"), """
            <?xml version="1.0" encoding="utf-8"?>
            <sanctionsData xmlns="https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/ENHANCED_XML">
              <entities>
                <entity id="501">
                  <generalInfo><entityType refId="600">Individual</entityType></generalInfo>
                  <sanctionsLists><sanctionsList datePublished="2023-01-02">SDN List</sanctionsList></sanctionsLists>
                  <sanctionsPrograms><sanctionsProgram>RUSSIA-EO14024</sanctionsProgram></sanctionsPrograms>
                  <names>
                    <name><isPrimary>true</isPrimary><isLowQuality>false</isLowQuality><translations>
                      <translation><isPrimary>true</isPrimary><script>Latin</script><formattedFirstName>Ivan</formattedFirstName><formattedLastName>IVANOV</formattedLastName><formattedFullName>IVANOV, Ivan</formattedFullName></translation>
                      <translation><isPrimary>false</isPrimary><script>Cyrillic</script><formattedFirstName>ИВАН</formattedFirstName><formattedLastName>ИВАНОВ</formattedLastName></translation>
                    </translations></name>
                    <name><isPrimary>false</isPrimary><isLowQuality>true</isLowQuality><translations>
                      <translation><isPrimary>true</isPrimary><script>Latin</script><formattedLastName>VANYA</formattedLastName><formattedFullName>VANYA</formattedFullName></translation>
                    </translations></name>
                  </names>
                  <addresses><address><country>Russia</country><translations><translation><addressParts>
                    <addressPart><type>CITY</type><value>Moscow</value></addressPart>
                  </addressParts></translation></translations></address></addresses>
                </entity>
                <entity id="502"><generalInfo><entityType>Vessel</entityType></generalInfo>
                  <names><name><isPrimary>true</isPrimary><translations><translation><formattedFullName>SHIP</formattedFullName></translation></translations></name></names>
                </entity>
              </entities>
            </sanctionsData>
            """);

        var person = Assert.Single(subjects);
        Assert.Equal(("501", SubjectType.Individual, "Ivan IVANOV"), (person.ExternalId, person.Type, person.PrimaryName));
        Assert.Equal(new DateOnly(2023, 1, 2), person.ListedOn);
        Assert.Equal("https://sanctionssearch.ofac.treas.gov/Details.aspx?id=501", person.SourceUrl);
        Assert.Contains(person.Names, name => name is { FullName: "ИВАН ИВАНОВ", Script: NameScript.Cyrillic });
        Assert.Contains(person.Names, name => name is { FullName: "VANYA", IsWeak: true });
        Assert.Equal("Moscow, Russia", Assert.Single(person.Addresses).Raw);
    }

    [Fact]
    public void Un_ReadsIndividualsAndEntities_LowQualityAliasIsWeak()
    {
        var subjects = Parse(new UnParser(), """
            <CONSOLIDATED_LIST dateGenerated="2026-11-06T00:00:00Z">
              <INDIVIDUALS><INDIVIDUAL>
                <DATAID>1</DATAID><FIRST_NAME>ANNA</FIRST_NAME><SECOND_NAME>PETROVA</SECOND_NAME>
                <UN_LIST_TYPE>DPRK</UN_LIST_TYPE><REFERENCE_NUMBER>KPi.999</REFERENCE_NUMBER><LISTED_ON>2016-03-02</LISTED_ON>
                <INDIVIDUAL_ALIAS><QUALITY>Low</QUALITY><ALIAS_NAME>Annushka</ALIAS_NAME></INDIVIDUAL_ALIAS>
                <INDIVIDUAL_ADDRESS><CITY>Pyongyang</CITY><COUNTRY>Democratic People's Republic of Korea</COUNTRY></INDIVIDUAL_ADDRESS>
              </INDIVIDUAL></INDIVIDUALS>
              <ENTITIES><ENTITY>
                <DATAID>2</DATAID><FIRST_NAME>NORTHWIND SHIPPING LLC</FIRST_NAME><REFERENCE_NUMBER>KPe.999</REFERENCE_NUMBER>
                <ENTITY_ALIAS><QUALITY /><ALIAS_NAME /></ENTITY_ALIAS><ENTITY_ADDRESS><COUNTRY /></ENTITY_ADDRESS>
              </ENTITY></ENTITIES>
            </CONSOLIDATED_LIST>
            """);

        Assert.Equal(2, subjects.Count);
        Assert.Equal(("KPi.999", SubjectType.Individual, "ANNA PETROVA"), (subjects[0].ExternalId, subjects[0].Type, subjects[0].PrimaryName));
        Assert.Contains(subjects[0].Names, name => name is { FullName: "Annushka", IsWeak: true });
        Assert.Equal("Pyongyang, Democratic People's Republic of Korea", Assert.Single(subjects[0].Addresses).Raw);
        Assert.Equal(("KPe.999", SubjectType.Entity), (subjects[1].ExternalId, subjects[1].Type));
        Assert.Single(subjects[1].Names);
        Assert.Empty(subjects[1].Addresses);
    }

    [Fact]
    public void Uk_JoinsNameParts_ReadsNonLatinNames_SkipsShips()
    {
        var subjects = Parse(new UkParser(), """
            <Designations>
              <Designation>
                <DateDesignated>15/03/2022</DateDesignated><UniqueID>RUS9999</UniqueID>
                <Names>
                  <Name><Name1>Hryhorii</Name1><Name6>HONCHAR</Name6><NameType>Primary Name</NameType></Name>
                  <Name><Name1>Grigoriy</Name1><Name6>GONCHAR</Name6><NameType>Alias</NameType><AliasStrength>Low quality a.k.a</AliasStrength></Name>
                </Names>
                <NonLatinNames><NonLatinName><NameNonLatinScript>Григорій Гончар / Григорий Гончар</NameNonLatinScript></NonLatinName></NonLatinNames>
                <RegimeName>The Russia (Sanctions) (EU Exit) Regulations 2019</RegimeName>
                <IndividualEntityShip>Individual</IndividualEntityShip>
                <Addresses><Address><AddressLine1>Khreshchatyk 1</AddressLine1><AddressLine6>Kyiv</AddressLine6><AddressCountry>Ukraine</AddressCountry></Address></Addresses>
              </Designation>
              <Designation><UniqueID>RUS0001</UniqueID><Names><Name><Name6>SEA STAR</Name6><NameType>Primary Name</NameType></Name></Names><IndividualEntityShip>Ship</IndividualEntityShip></Designation>
            </Designations>
            """);

        var person = Assert.Single(subjects);
        Assert.Equal(("RUS9999", SubjectType.Individual, "Hryhorii HONCHAR"), (person.ExternalId, person.Type, person.PrimaryName));
        Assert.Equal(new DateOnly(2022, 3, 15), person.ListedOn);
        Assert.Contains(person.Names, name => name is { FullName: "Grigoriy GONCHAR", IsWeak: true });
        Assert.Contains(person.Names, name => name is { FullName: "Григорій Гончар", Script: NameScript.Cyrillic });
        Assert.Contains(person.Names, name => name is { FullName: "Григорий Гончар", Script: NameScript.Cyrillic });
        Assert.Equal("Khreshchatyk 1, Kyiv, Ukraine", Assert.Single(person.Addresses).Raw);
    }

    [Fact]
    public void UsExport_KeepsOnlyCommerceAndState_AsUnknownType()
    {
        var subjects = Parse(new UsExportParser(), """
            {"results": [
              {"id": "a1", "source": "Entity List (EL) - Bureau of Industry and Security", "name": "Volga Trade", "alt_names": ["Volga Trading"],
               "addresses": [{"address": "Lenina 5", "city": "Moscow", "state": "", "postal_code": "", "country": "RU"}],
               "start_date": "2022-03-03", "source_list_url": "https://www.bis.gov/x"},
              {"id": "a2", "source": "Specially Designated Nationals (SDN) - Treasury Department", "name": "Repeated From OFAC"},
              {"id": "a3", "source": "ITAR Debarred (DTC) - State Department", "name": "Alpha Beta", "type": null},
              {"id": "a4", "source": "Entity List (EL) - Bureau of Industry and Security", "name": "Some Vessel", "type": "Vessel"}
            ], "total": 4}
            """);

        Assert.Equal(["a1", "a3"], subjects.Select(subject => subject.ExternalId));
        Assert.All(subjects, subject => Assert.Equal(SubjectType.Unknown, subject.Type));
        Assert.Equal(["Volga Trade", "Volga Trading"], subjects[0].Names.Select(name => name.FullName));
        Assert.Equal(new ParsedAddress("Lenina 5, Moscow, RU", "RU"), Assert.Single(subjects[0].Addresses));
        Assert.Equal("https://www.bis.gov/x", subjects[0].SourceUrl);
    }

    [Fact]
    public void Seco_SkipsDelistedTargetsAndObjects_ResolvesPlaces_ReadsSpellingVariants()
    {
        var subjects = Parse(new SecoParser(), """
            <swiss-sanctions-list list-type="whole-list" date="2026-11-04">
              <sanctions-program ssid="20"><program-key lang="eng">Belarus</program-key><sanctions-set ssid="4387" lang="eng">annex 13</sanctions-set></sanctions-program>
              <target ssid="100"><sanctions-set-id>4387</sanctions-set-id>
                <individual><identity ssid="101" main="true">
                  <name name-type="primary-name" quality="good"><name-part order="2" name-part-type="given-name"><value>Ivan</value><spelling-variant lang="RUS" script="CYRL">Иван</spelling-variant></name-part>
                    <name-part order="1" name-part-type="family-name"><value>Sokolov</value><spelling-variant lang="RUS" script="CYRL">Соколов</spelling-variant></name-part></name>
                  <address place-id="7"><address-details>Lenina 5</address-details></address>
                </identity></individual>
                <modification modification-type="listed" effective-date="2021-06-21" />
              </target>
              <target ssid="200"><entity><identity main="true"><name name-type="primary-name"><name-part order="1"><value>Gone LLC</value></name-part></name></identity></entity>
                <modification modification-type="de-listed" effective-date="2016-03-01" /><modification modification-type="listed" />
              </target>
              <target ssid="300"><object><identity main="true"><name><name-part><value>SEA STAR</value></name-part></name></identity></object></target>
              <place ssid="7"><location>Minsk</location><country iso-code="BY">Belarus</country></place>
            </swiss-sanctions-list>
            """);

        var person = Assert.Single(subjects);
        Assert.Equal(("100", SubjectType.Individual, "Sokolov Ivan", "Belarus"), (person.ExternalId, person.Type, person.PrimaryName, person.Programmes));
        Assert.Equal(new DateOnly(2021, 6, 21), person.ListedOn);
        Assert.Contains(person.Names, name => name is { FullName: "Соколов Иван", Script: NameScript.Cyrillic });
        Assert.Equal(new ParsedAddress("Lenina 5, Minsk, Belarus", "BY"), Assert.Single(person.Addresses));
    }

    [Fact]
    public void CanadaSema_SplitsLabelledAliases_SkipsShips()
    {
        var subjects = Parse(new CanadaSemaParser(), """
            <data-set>
              <record><Country-Pays>Belarus / Bélarus</Country-Pays><LastName-NomDeFamille>Kovalenko</LastName-NomDeFamille><GivenName-Prenom>Roman</GivenName-Prenom>
                <Aliases-Alias>Raman Kavalenka, Belarusian: Раман Каваленка;  Russian: Роман Коваленко</Aliases-Alias>
                <Schedule-Annexe>1, Part 1</Schedule-Annexe><Item-NumeroDarticle>12</Item-NumeroDarticle><DateOfListing-DateDinscription>2020-09-28</DateOfListing-DateDinscription></record>
              <record><Country-Pays>Russia / Russie</Country-Pays><EntityOrShip-EntiteOuNavire>Volga Trade</EntityOrShip-EntiteOuNavire><Schedule-Annexe>1, Part 2</Schedule-Annexe><Item-NumeroDarticle>3</Item-NumeroDarticle></record>
              <record><Country-Pays>Russia / Russie</Country-Pays><EntityOrShip-EntiteOuNavire>Sea Star</EntityOrShip-EntiteOuNavire><ShipIMONumber-NumeroOMIDuNavire>9262924</ShipIMONumber-NumeroOMIDuNavire><Item-NumeroDarticle>69</Item-NumeroDarticle></record>
            </data-set>
            """);

        Assert.Equal(2, subjects.Count);
        var person = subjects[0];
        Assert.Equal(("Belarus / Bélarus/1, Part 1/12", SubjectType.Individual, "Roman Kovalenko"), (person.ExternalId, person.Type, person.PrimaryName));
        Assert.Equal(["Roman Kovalenko", "Raman Kavalenka", "Раман Каваленка", "Роман Коваленко"], person.Names.Select(name => name.FullName));
        Assert.Equal(("Volga Trade", SubjectType.Entity), (subjects[1].PrimaryName, subjects[1].Type));
    }

    [Fact]
    public void Sfms_ReadsTypesAndAliasQuality()
    {
        var subjects = Parse(new SfmsParser(), """
            <list-terror><count-record>2</count-record>
              <acount-list><number-entry>10</number-entry><date-entry>20230317</date-entry><type-entry>1</type-entry><program-entry>РБ ООН</program-entry>
                <aka-list><aka-name1>NORTHWIND TRUST</aka-name1><type-aka>N</type-aka></aka-list>
                <aka-list><aka-name1>Northwind Relief</aka-name1><type-aka>PE</type-aka><quality-aka>2</quality-aka></aka-list>
                <address-list><country-code>586</country-code><address>Karachi, Pakistan</address></address-list></acount-list>
              <acount-list><number-entry>11</number-entry><type-entry>2</type-entry>
                <aka-list><aka-name1>Шевченко</aka-name1><aka-name2>Тарас</aka-name2><type-aka>N</type-aka></aka-list></acount-list>
            </list-terror>
            """);

        Assert.Equal(2, subjects.Count);
        Assert.Equal(("10", SubjectType.Entity, "NORTHWIND TRUST"), (subjects[0].ExternalId, subjects[0].Type, subjects[0].PrimaryName));
        Assert.Contains(subjects[0].Names, name => name is { FullName: "Northwind Relief", IsWeak: true });
        Assert.Equal(new DateOnly(2023, 3, 17), subjects[0].ListedOn);
        Assert.Equal("Karachi, Pakistan", Assert.Single(subjects[0].Addresses).Raw);
        Assert.Equal(("Шевченко Тарас", SubjectType.Individual, NameScript.Cyrillic), (subjects[1].PrimaryName, subjects[1].Type, subjects[1].Names[0].Script));
    }

    [Fact]
    public void France_ReadsPersonsAndCompanies_LeavesOutVessels_MarksWeakAliases()
    {
        var subjects = Parse(new FranceParser(), """
            {"Publications": {"DatePublication": "2026-11-16T10:47:40+01:00", "PublicationDetail": [
              {"IdRegistre": 101, "Nature": "Personne physique", "Nom": "SOKOLOV", "RegistreDetail": [
                {"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan Petrovich"}]},
                {"TypeChamp": "ALIAS", "Valeur": [{"Alias": "Иван Петрович СОКОЛОВ", "Commentaire": "en russe"}, {"Alias": "Vanya", "Commentaire": "alias faible"}]},
                {"TypeChamp": "ADRESSE_PP", "Valeur": [{"Adresse": "Lenina 5, Moscou", "Pays": "RUSSIE"}]},
                {"TypeChamp": "MOTIFS", "Valeur": [{"Motifs": "Membre du conseil."}]},
                {"TypeChamp": "FONDEMENT_JURIDIQUE", "Valeur": [{"FondementJuridique": 1, "FondementJuridiqueLabel": "(UE) 2022/332"}, {"FondementJuridique": 2, "FondementJuridiqueLabel": "(UE) 2025/1894"}]}]},
              {"IdRegistre": 102, "Nature": "Personne morale", "Nom": "Northwind Trading SARL", "RegistreDetail": [
                {"TypeChamp": "ADRESSE_PM", "Valeur": [{"Adresse": "12 rue de la Paix, Paris", "Pays": "FRANCE"}]}]},
              {"IdRegistre": 103, "Nature": "Navire", "Nom": "SEA STAR", "RegistreDetail": [{"TypeChamp": "NUMERO_OMI", "Valeur": [{"NumeroOMI": "1234567"}]}]}
            ]}}
            """);

        Assert.Equal(["101", "102"], subjects.Select(subject => subject.ExternalId));
        var person = subjects[0];
        Assert.Equal((SubjectType.Individual, "Ivan Petrovich SOKOLOV"), (person.Type, person.PrimaryName));
        Assert.Contains(person.Names, name => name is { FullName: "Иван Петрович СОКОЛОВ", IsWeak: false, Script: NameScript.Cyrillic });
        Assert.Contains(person.Names, name => name is { FullName: "Vanya", IsWeak: true });
        Assert.Equal(new ParsedAddress("Lenina 5, Moscou, RUSSIE", null), Assert.Single(person.Addresses));
        Assert.Equal(("(UE) 2022/332; (UE) 2025/1894", "Membre du conseil."), (person.Programmes, person.Remarks));
        Assert.Equal(FranceParser.RecordUrl + "101", person.SourceUrl);
        Assert.Equal((SubjectType.Entity, "Northwind Trading SARL"), (subjects[1].Type, subjects[1].PrimaryName));
    }

    [Theory]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A"}]}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"Nature": "Personne morale", "Nom": "A"}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A"}, 7]}}""")]
    [InlineData("""{"Publications": {}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne physique", "Nom": "A", "RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}, {"Prenom": 7}]}]}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale"}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne physique", "Nom": " ", "RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}]}]}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": {"TypeChamp": "ALIAS"}}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": {"Alias": "B"}}]}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"Valeur": [{"Alias": "B"}]}]}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": ["B"]}]}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": ["ALIAS"]}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": [{"Alias": 7}]}]}]}}""")]
    [InlineData("""{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ADRESSE_PM", "Valeur": [{"Pays": "FRANCE"}]}]}]}}""")]
    public void France_RejectsMalformedDocuments(string json)
    {
        Assert.Throws<ListFormatException>(() => Parse(new FranceParser(), json));
    }

    private static readonly string[] JapanHeader =
    [
        "区分", "番号", "告示日付", "告示番号", "個人・団体", "氏名（日本語）", "氏名（英語）", "別名・別称（日本語）", "別名・別称（英語）", "旧称（日本語）", "旧称（英語）",
        "確定に十分でない別名（日本語）", "確定に十分でない別名（英語）", "称号（日本語）", "称号（英語）", "役職（日本語）", "役職（英語）", "生年月日", "出生地（日本語）", "出生地（英語）",
        "国籍（日本語）", "国籍（英語）", "旅券番号", "身分証番号", "住所・所在地（国）（日本語）", "住所・所在地（都市その他の情報）（日本語）", "住所・所在地（国）（英語）",
        "住所・所在地（都市その他の情報）（英語）", "国連参照番号", "リスト掲載日", "その他の情報", "外務省告示情報",
    ];

    /// <summary>A row of Japan's list: the named columns filled, quoted as the CSV quotes them, the others empty.</summary>
    private static string JapanRow(params (string Column, string Value)[] cells) =>
        string.Join(',', JapanHeader.Select(column => cells.FirstOrDefault(cell => cell.Column == column).Value is { } value ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : string.Empty));

    [Fact]
    public void Japan_ReadsPeopleAndGroups_InEnglishAndJapanese_WeakAliases_AndPairedAddresses()
    {
        var subjects = Parse(new JapanParser(), "\uFEFF" + string.Join("\r\n",
            string.Join(',', JapanHeader),
            JapanRow(("区分", "29"), ("番号", "029-000001"), ("告示日付", "2022.3.8 2023.1.10改訂"), ("個人・団体", "個人"), ("氏名（日本語）", "ソコロフ・イワン"),
                ("氏名（英語）", "Ivan SOKOLOV; Ivan Petrovich Sokolov"), ("別名・別称（英語）", "Vanya Sokolov；I. Sokolov"), ("確定に十分でない別名（英語）", "Big Ivan"),
                ("生年月日", "1970/1/2"), ("国籍（英語）", "Russia"), ("住所・所在地（国）（英語）", "Russian Federation(リスト掲載時点); Cyprus"),
                ("住所・所在地（都市その他の情報）（英語）", "Moscow,; Limassol,"), ("住所・所在地（国）（日本語）", "ロシア")),
            new string(',', JapanHeader.Length - 1),
            JapanRow(("区分", "2"), ("番号", "002-000005"), ("告示日付", "2001.9.22"), ("個人・団体", "団体"), ("氏名（英語）", "Northwind Trading LLC"),
                ("旧称（英語）", "Northwind Export LLC"), ("住所・所在地（国）（日本語）", "日本"), ("住所・所在地（都市その他の情報）（日本語）", "東京")),
            JapanRow(("番号", "044-000001"), ("個人・団体", "その他"), ("氏名（英語）", "UNNAMED GANG"), ("住所・所在地（国）（英語）", "Haiti; Haiti"),
                ("住所・所在地（都市その他の情報）（英語）", "Port-au-Prince")),
            string.Empty));

        Assert.Equal(["029-000001", "002-000005", "044-000001"], subjects.Select(subject => subject.ExternalId));
        var person = subjects[0];
        Assert.Equal((SubjectType.Individual, "Ivan SOKOLOV"), (person.Type, person.PrimaryName));
        Assert.Equal(
            [("Ivan SOKOLOV", false), ("Ivan Petrovich Sokolov", false), ("ソコロフ・イワン", false), ("Vanya Sokolov", false), ("I. Sokolov", false), ("Big Ivan", true)],
            person.Names.Select(name => (name.FullName, name.IsWeak)));
        Assert.Equal(NameScript.Other, person.Names.Single(name => name.FullName == "ソコロフ・イワン").Script);
        Assert.Equal([new ParsedAddress("Moscow, Russian Federation", null), new ParsedAddress("Limassol, Cyprus", null)], person.Addresses);
        Assert.Equal((JapanParser.Programme + ", category 29", new DateOnly(2022, 3, 8), "born 1970/1/2; nationality Russia"), (person.Programmes, person.ListedOn, person.Remarks));
        Assert.Null(person.SourceUrl);

        var group = subjects[1];
        Assert.Equal((SubjectType.Entity, "Northwind Trading LLC", new DateOnly(2001, 9, 22)), (group.Type, group.PrimaryName, group.ListedOn));
        Assert.Contains(group.Names, name => name.FullName == "Northwind Export LLC");
        Assert.Equal(new ParsedAddress("東京, 日本", null), Assert.Single(group.Addresses));

        // Not a person or a group as the list writes them; a city and countries that don't pair are kept each on its own.
        Assert.Equal((SubjectType.Unknown, JapanParser.Programme), (subjects[2].Type, subjects[2].Programmes));
        Assert.Equal([new ParsedAddress("Port-au-Prince", null), new ParsedAddress("Haiti", null)], subjects[2].Addresses);
    }

    [Fact]
    public void Japan_WithoutItsColumns_OrARowWithoutItsNumber_IsAFormatError()
    {
        Assert.Throws<ListFormatException>(() => Parse(new JapanParser(), "番号,氏名（英語）\r\n1,A\r\n"));
        Assert.Throws<ListFormatException>(() => Parse(new JapanParser(), string.Join(',', JapanHeader) + "\r\n" + JapanRow(("氏名（英語）", "A")) + "\r\n"));
    }

    [Theory]
    [InlineData("""{"results": [{"id": "a1", "source": "Entity List (EL) - Bureau of Industry and Security", "name": "A"}, null]}""")]
    [InlineData("""{"results": [{"id": "a1", "source": "Entity List (EL) - Bureau of Industry and Security", "name": "A"}]""")]
    [InlineData("""{"total": 0}""")]
    public void UsExport_RejectsMalformedDocuments(string json)
    {
        Assert.ThrowsAny<Exception>(() => Parse(new UsExportParser(), json));
    }

    [Fact]
    public void Seco_KeepsEverySpellingWithinALanguage()
    {
        var subjects = Parse(new SecoParser(), """
            <swiss-sanctions-list>
              <target ssid="1"><individual><identity main="true"><name name-type="primary-name">
                <name-part order="1"><value>Ivan</value><spelling-variant lang="RUS" script="CYRL">Иван</spelling-variant></name-part>
                <name-part order="2"><value>Gonchar</value><spelling-variant lang="RUS" script="CYRL">Гончар</spelling-variant><spelling-variant lang="RUS" script="CYRL">Ганчар</spelling-variant></name-part>
              </name></identity></individual></target>
            </swiss-sanctions-list>
            """);

        Assert.Equal(["Ivan Gonchar", "Иван Гончар", "Иван Ганчар"], Assert.Single(subjects).Names.Select(name => name.FullName));
    }

    [Fact]
    public void Seco_ManySpellings_EverySpellingStillAppears()
    {
        var familyNames = Enumerable.Range(1, 9).Select(i => $"Фамилия{i}").ToList();
        var variants = string.Concat(familyNames.Select(name => $"""<spelling-variant lang="RUS" script="CYRL">{name}</spelling-variant>"""));
        var subjects = Parse(new SecoParser(), $"""
            <swiss-sanctions-list>
              <target ssid="1"><individual><identity main="true"><name name-type="primary-name">
                <name-part order="1"><value>Ivan</value><spelling-variant lang="RUS" script="CYRL">Иван</spelling-variant></name-part>
                <name-part order="2"><value>Familiya</value>{variants}</name-part>
              </name></identity></individual></target>
            </swiss-sanctions-list>
            """);

        var names = Assert.Single(subjects).Names.Select(name => name.FullName).ToList();
        Assert.All(familyNames, family => Assert.Contains($"Иван {family}", names));
    }

    [Fact]
    public void RepeatedName_IsStrongIfAnyListingIsStrong()
    {
        var subjects = Parse(new UnParser(), """
            <CONSOLIDATED_LIST><INDIVIDUALS><INDIVIDUAL>
              <REFERENCE_NUMBER>X.1</REFERENCE_NUMBER><FIRST_NAME>ANNA</FIRST_NAME>
              <INDIVIDUAL_ALIAS><QUALITY>Low</QUALITY><ALIAS_NAME>Annushka</ALIAS_NAME></INDIVIDUAL_ALIAS>
              <INDIVIDUAL_ALIAS><QUALITY>Good</QUALITY><ALIAS_NAME>Annushka</ALIAS_NAME></INDIVIDUAL_ALIAS>
            </INDIVIDUAL></INDIVIDUALS></CONSOLIDATED_LIST>
            """);

        Assert.Contains(Assert.Single(subjects).Names, name => name is { FullName: "Annushka", IsWeak: false });
    }

    [Fact]
    public void WrongRootElement_IsAFormatError()
    {
        Assert.Throws<ListFormatException>(() => Parse(new EuParser(), "<html><body>Maintenance</body></html>"));
    }

    [Theory]
    [InlineData("Ivan Ivanov", NameScript.Latin)]
    [InlineData("Іван Іваненко", NameScript.Cyrillic)]
    [InlineData("OOО Ромашка", NameScript.Cyrillic)]
    [InlineData("هادی منصوری", NameScript.Other)]
    [InlineData("Ştefan Ţărănu", NameScript.Latin)]
    public void DetectScript_ClassifiesNames(string name, NameScript expected)
    {
        Assert.Equal(expected, Text.DetectScript(name));
    }
}
