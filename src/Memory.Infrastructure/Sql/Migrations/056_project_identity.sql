-- Frozen mapping fingerprint: C738366E5613E67DCB077E25D7F6CCCDD2E9EDC1FADEF7DF15A4D3E9CEC53513
-- Identity v1 preserves ProjectId spelling. Only comparison/index keys are folded.
-- Unicode simple-uppercase and whitespace maps are frozen from .NET 10.0.3.
-- Do not replace this with locale-dependent lower()/ICU normalization: those may
-- introduce additional aliases. Changing the frozen map requires a new contract.
-- PostgreSQL functions match ProjectContext.IdentityKey/Matches. No data is rewritten.
CREATE OR REPLACE FUNCTION public.project_identity_key(value text) RETURNS text
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $project_identity_v1$
    -- Resolve common ASCII before the frozen Unicode pairs; unchanged ASCII must not scan the full map.
    SELECT translate(btrim(value, '	

                  　'),
        'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_:- ' || E'\001\002\003\004\005\006\007\010\011\012\013\014\015\016\017\020\021\022\023\024\025\026\027\030\031\032\033\034\035\036\037\041\042\043\044\045\046\047\050\051\052\053\054\056\057\073\074\075\076\077\100\133\134\135\136\140\173\174\175\176\177' || 'abcdefghijklmnopqrstuvwxyzµàáâãäåæçèéêëìíîïðñòóôõöøùúûüýþÿāăąćĉċčďđēĕėęěĝğġģĥħĩīĭįĳĵķĺļľŀłńņňŋōŏőœŕŗřśŝşšţťŧũūŭůűųŵŷźżžſƀƃƅƈƌƒƕƙƚƞơƣƥƨƭưƴƶƹƽƿǅǆǈǉǋǌǎǐǒǔǖǘǚǜǝǟǡǣǥǧǩǫǭǯǲǳǵǹǻǽǿȁȃȅȇȉȋȍȏȑȓȕȗșțȝȟȣȥȧȩȫȭȯȱȳȼȿɀɂɇɉɋɍɏɐɑɒɓɔɖɗəɛɜɠɡɣɥɦɨɩɪɫɬɯɱɲɵɽʀʂʃʇʈʉʊʋʌʒʝʞͅͱͳͷͻͼͽάέήίαβγδεζηθικλμνξοπρςστυφχψωϊϋόύώϐϑϕϖϗϙϛϝϟϡϣϥϧϩϫϭϯϰϱϲϳϵϸϻабвгдежзийклмнопрстуфхцчшщъыьэюяѐёђѓєѕіїјљњћќѝўџѡѣѥѧѩѫѭѯѱѳѵѷѹѻѽѿҁҋҍҏґғҕҗҙқҝҟҡңҥҧҩҫҭүұҳҵҷҹһҽҿӂӄӆӈӊӌӎӏӑӓӕӗәӛӝӟӡӣӥӧөӫӭӯӱӳӵӷӹӻӽӿԁԃԅԇԉԋԍԏԑԓԕԗԙԛԝԟԡԣԥԧԩԫԭԯաբգդեզէըթժիլխծկհձղճմյնշոչպջռսվտրցւփքօֆაბგდევზთიკლმნოპჟრსტუფქღყშჩცძწჭხჯჰჱჲჳჴჵჶჷჸჹჺჽჾჿᏸᏹᏺᏻᏼᏽᲀᲁᲂᲃᲄᲅᲆᲇᲈᵹᵽᶎḁḃḅḇḉḋḍḏḑḓḕḗḙḛḝḟḡḣḥḧḩḫḭḯḱḳḵḷḹḻḽḿṁṃṅṇṉṋṍṏṑṓṕṗṙṛṝṟṡṣṥṧṩṫṭṯṱṳṵṷṹṻṽṿẁẃẅẇẉẋẍẏẑẓẕẛạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹỻỽỿἀἁἂἃἄἅἆἇἐἑἒἓἔἕἠἡἢἣἤἥἦἧἰἱἲἳἴἵἶἷὀὁὂὃὄὅὑὓὕὗὠὡὢὣὤὥὦὧὰάὲέὴήὶίὸόὺύὼώᾀᾁᾂᾃᾄᾅᾆᾇᾐᾑᾒᾓᾔᾕᾖᾗᾠᾡᾢᾣᾤᾥᾦᾧᾰᾱᾳιῃῐῑῠῡῥῳⅎⅰⅱⅲⅳⅴⅵⅶⅷⅸⅹⅺⅻⅼⅽⅾⅿↄⓐⓑⓒⓓⓔⓕⓖⓗⓘⓙⓚⓛⓜⓝⓞⓟⓠⓡⓢⓣⓤⓥⓦⓧⓨⓩⰰⰱⰲⰳⰴⰵⰶⰷⰸⰹⰺⰻⰼⰽⰾⰿⱀⱁⱂⱃⱄⱅⱆⱇⱈⱉⱊⱋⱌⱍⱎⱏⱐⱑⱒⱓⱔⱕⱖⱗⱘⱙⱚⱛⱜⱝⱞⱟⱡⱥⱦⱨⱪⱬⱳⱶⲁⲃⲅⲇⲉⲋⲍⲏⲑⲓⲕⲗⲙⲛⲝⲟⲡⲣⲥⲧⲩⲫⲭⲯⲱⲳⲵⲷⲹⲻⲽⲿⳁⳃⳅⳇⳉⳋⳍⳏⳑⳓⳕⳗⳙⳛⳝⳟⳡⳣⳬⳮⳳⴀⴁⴂⴃⴄⴅⴆⴇⴈⴉⴊⴋⴌⴍⴎⴏⴐⴑⴒⴓⴔⴕⴖⴗⴘⴙⴚⴛⴜⴝⴞⴟⴠⴡⴢⴣⴤⴥⴧⴭꙁꙃꙅꙇꙉꙋꙍꙏꙑꙓꙕꙗꙙꙛꙝꙟꙡꙣꙥꙧꙩꙫꙭꚁꚃꚅꚇꚉꚋꚍꚏꚑꚓꚕꚗꚙꚛꜣꜥꜧꜩꜫꜭꜯꜳꜵꜷꜹꜻꜽꜿꝁꝃꝅꝇꝉꝋꝍꝏꝑꝓꝕꝗꝙꝛꝝꝟꝡꝣꝥꝧꝩꝫꝭꝯꝺꝼꝿꞁꞃꞅꞇꞌꞑꞓꞔꞗꞙꞛꞝꞟꞡꞣꞥꞧꞩꞵꞷꞹꞻꞽꞿꟁꟃꟈꟊꟑꟗꟙꟶꭓꭰꭱꭲꭳꭴꭵꭶꭷꭸꭹꭺꭻꭼꭽꭾꭿꮀꮁꮂꮃꮄꮅꮆꮇꮈꮉꮊꮋꮌꮍꮎꮏꮐꮑꮒꮓꮔꮕꮖꮗꮘꮙꮚꮛꮜꮝꮞꮟꮠꮡꮢꮣꮤꮥꮦꮧꮨꮩꮪꮫꮬꮭꮮꮯꮰꮱꮲꮳꮴꮵꮶꮷꮸꮹꮺꮻꮼꮽꮾꮿａｂｃｄｅｆｇｈｉｊｋｌｍｎｏｐｑｒｓｔｕｖｗｘｙｚ𐐨𐐩𐐪𐐫𐐬𐐭𐐮𐐯𐐰𐐱𐐲𐐳𐐴𐐵𐐶𐐷𐐸𐐹𐐺𐐻𐐼𐐽𐐾𐐿𐑀𐑁𐑂𐑃𐑄𐑅𐑆𐑇𐑈𐑉𐑊𐑋𐑌𐑍𐑎𐑏𐓘𐓙𐓚𐓛𐓜𐓝𐓞𐓟𐓠𐓡𐓢𐓣𐓤𐓥𐓦𐓧𐓨𐓩𐓪𐓫𐓬𐓭𐓮𐓯𐓰𐓱𐓲𐓳𐓴𐓵𐓶𐓷𐓸𐓹𐓺𐓻𐖗𐖘𐖙𐖚𐖛𐖜𐖝𐖞𐖟𐖠𐖡𐖣𐖤𐖥𐖦𐖧𐖨𐖩𐖪𐖫𐖬𐖭𐖮𐖯𐖰𐖱𐖳𐖴𐖵𐖶𐖷𐖸𐖹𐖻𐖼𐳀𐳁𐳂𐳃𐳄𐳅𐳆𐳇𐳈𐳉𐳊𐳋𐳌𐳍𐳎𐳏𐳐𐳑𐳒𐳓𐳔𐳕𐳖𐳗𐳘𐳙𐳚𐳛𐳜𐳝𐳞𐳟𐳠𐳡𐳢𐳣𐳤𐳥𐳦𐳧𐳨𐳩𐳪𐳫𐳬𐳭𐳮𐳯𐳰𐳱𐳲𑣀𑣁𑣂𑣃𑣄𑣅𑣆𑣇𑣈𑣉𑣊𑣋𑣌𑣍𑣎𑣏𑣐𑣑𑣒𑣓𑣔𑣕𑣖𑣗𑣘𑣙𑣚𑣛𑣜𑣝𑣞𑣟𖹠𖹡𖹢𖹣𖹤𖹥𖹦𖹧𖹨𖹩𖹪𖹫𖹬𖹭𖹮𖹯𖹰𖹱𖹲𖹳𖹴𖹵𖹶𖹷𖹸𖹹𖹺𖹻𖹼𖹽𖹾𖹿𞤢𞤣𞤤𞤥𞤦𞤧𞤨𞤩𞤪𞤫𞤬𞤭𞤮𞤯𞤰𞤱𞤲𞤳𞤴𞤵𞤶𞤷𞤸𞤹𞤺𞤻𞤼𞤽𞤾𞤿𞥀𞥁𞥂𞥃',
        'ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_:- ' || E'\001\002\003\004\005\006\007\010\011\012\013\014\015\016\017\020\021\022\023\024\025\026\027\030\031\032\033\034\035\036\037\041\042\043\044\045\046\047\050\051\052\053\054\056\057\073\074\075\076\077\100\133\134\135\136\140\173\174\175\176\177' || 'ABCDEFGHIJKLMNOPQRSTUVWXYZΜÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞŸĀĂĄĆĈĊČĎĐĒĔĖĘĚĜĞĠĢĤĦĨĪĬĮĲĴĶĹĻĽĿŁŃŅŇŊŌŎŐŒŔŖŘŚŜŞŠŢŤŦŨŪŬŮŰŲŴŶŹŻŽSɃƂƄƇƋƑǶƘȽȠƠƢƤƧƬƯƳƵƸƼǷǄǄǇǇǊǊǍǏǑǓǕǗǙǛƎǞǠǢǤǦǨǪǬǮǱǱǴǸǺǼǾȀȂȄȆȈȊȌȎȐȒȔȖȘȚȜȞȢȤȦȨȪȬȮȰȲȻⱾⱿɁɆɈɊɌɎⱯⱭⱰƁƆƉƊƏƐꞫƓꞬƔꞍꞪƗƖꞮⱢꞭƜⱮƝƟⱤƦꟅƩꞱƮɄƱƲɅƷꞲꞰΙͰͲͶϽϾϿΆΈΉΊΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΣΤΥΦΧΨΩΪΫΌΎΏΒΘΦΠϏϘϚϜϞϠϢϤϦϨϪϬϮΚΡϹͿΕϷϺАБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯЀЁЂЃЄЅІЇЈЉЊЋЌЍЎЏѠѢѤѦѨѪѬѮѰѲѴѶѸѺѼѾҀҊҌҎҐҒҔҖҘҚҜҞҠҢҤҦҨҪҬҮҰҲҴҶҸҺҼҾӁӃӅӇӉӋӍӀӐӒӔӖӘӚӜӞӠӢӤӦӨӪӬӮӰӲӴӶӸӺӼӾԀԂԄԆԈԊԌԎԐԒԔԖԘԚԜԞԠԢԤԦԨԪԬԮԱԲԳԴԵԶԷԸԹԺԻԼԽԾԿՀՁՂՃՄՅՆՇՈՉՊՋՌՍՎՏՐՑՒՓՔՕՖᲐᲑᲒᲓᲔᲕᲖᲗᲘᲙᲚᲛᲜᲝᲞᲟᲠᲡᲢᲣᲤᲥᲦᲧᲨᲩᲪᲫᲬᲭᲮᲯᲰᲱᲲᲳᲴᲵᲶᲷᲸᲹᲺᲽᲾᲿᏰᏱᏲᏳᏴᏵВДОСТТЪѢꙊꝽⱣꟆḀḂḄḆḈḊḌḎḐḒḔḖḘḚḜḞḠḢḤḦḨḪḬḮḰḲḴḶḸḺḼḾṀṂṄṆṈṊṌṎṐṒṔṖṘṚṜṞṠṢṤṦṨṪṬṮṰṲṴṶṸṺṼṾẀẂẄẆẈẊẌẎẐẒẔṠẠẢẤẦẨẪẬẮẰẲẴẶẸẺẼẾỀỂỄỆỈỊỌỎỐỒỔỖỘỚỜỞỠỢỤỦỨỪỬỮỰỲỴỶỸỺỼỾἈἉἊἋἌἍἎἏἘἙἚἛἜἝἨἩἪἫἬἭἮἯἸἹἺἻἼἽἾἿὈὉὊὋὌὍὙὛὝὟὨὩὪὫὬὭὮὯᾺΆῈΈῊΉῚΊῸΌῪΎῺΏᾈᾉᾊᾋᾌᾍᾎᾏᾘᾙᾚᾛᾜᾝᾞᾟᾨᾩᾪᾫᾬᾭᾮᾯᾸᾹᾼΙῌῘῙῨῩῬῼℲⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫⅬⅭⅮⅯↃⒶⒷⒸⒹⒺⒻⒼⒽⒾⒿⓀⓁⓂⓃⓄⓅⓆⓇⓈⓉⓊⓋⓌⓍⓎⓏⰀⰁⰂⰃⰄⰅⰆⰇⰈⰉⰊⰋⰌⰍⰎⰏⰐⰑⰒⰓⰔⰕⰖⰗⰘⰙⰚⰛⰜⰝⰞⰟⰠⰡⰢⰣⰤⰥⰦⰧⰨⰩⰪⰫⰬⰭⰮⰯⱠȺȾⱧⱩⱫⱲⱵⲀⲂⲄⲆⲈⲊⲌⲎⲐⲒⲔⲖⲘⲚⲜⲞⲠⲢⲤⲦⲨⲪⲬⲮⲰⲲⲴⲶⲸⲺⲼⲾⳀⳂⳄⳆⳈⳊⳌⳎⳐⳒⳔⳖⳘⳚⳜⳞⳠⳢⳫⳭⳲႠႡႢႣႤႥႦႧႨႩႪႫႬႭႮႯႰႱႲႳႴႵႶႷႸႹႺႻႼႽႾႿჀჁჂჃჄჅჇჍꙀꙂꙄꙆꙈꙊꙌꙎꙐꙒꙔꙖꙘꙚꙜꙞꙠꙢꙤꙦꙨꙪꙬꚀꚂꚄꚆꚈꚊꚌꚎꚐꚒꚔꚖꚘꚚꜢꜤꜦꜨꜪꜬꜮꜲꜴꜶꜸꜺꜼꜾꝀꝂꝄꝆꝈꝊꝌꝎꝐꝒꝔꝖꝘꝚꝜꝞꝠꝢꝤꝦꝨꝪꝬꝮꝹꝻꝾꞀꞂꞄꞆꞋꞐꞒꟄꞖꞘꞚꞜꞞꞠꞢꞤꞦꞨꞴꞶꞸꞺꞼꞾꟀꟂꟇꟉꟐꟖꟘꟵꞳᎠᎡᎢᎣᎤᎥᎦᎧᎨᎩᎪᎫᎬᎭᎮᎯᎰᎱᎲᎳᎴᎵᎶᎷᎸᎹᎺᎻᎼᎽᎾᎿᏀᏁᏂᏃᏄᏅᏆᏇᏈᏉᏊᏋᏌᏍᏎᏏᏐᏑᏒᏓᏔᏕᏖᏗᏘᏙᏚᏛᏜᏝᏞᏟᏠᏡᏢᏣᏤᏥᏦᏧᏨᏩᏪᏫᏬᏭᏮᏯＡＢＣＤＥＦＧＨＩＪＫＬＭＮＯＰＱＲＳＴＵＶＷＸＹＺ𐐀𐐁𐐂𐐃𐐄𐐅𐐆𐐇𐐈𐐉𐐊𐐋𐐌𐐍𐐎𐐏𐐐𐐑𐐒𐐓𐐔𐐕𐐖𐐗𐐘𐐙𐐚𐐛𐐜𐐝𐐞𐐟𐐠𐐡𐐢𐐣𐐤𐐥𐐦𐐧𐒰𐒱𐒲𐒳𐒴𐒵𐒶𐒷𐒸𐒹𐒺𐒻𐒼𐒽𐒾𐒿𐓀𐓁𐓂𐓃𐓄𐓅𐓆𐓇𐓈𐓉𐓊𐓋𐓌𐓍𐓎𐓏𐓐𐓑𐓒𐓓𐕰𐕱𐕲𐕳𐕴𐕵𐕶𐕷𐕸𐕹𐕺𐕼𐕽𐕾𐕿𐖀𐖁𐖂𐖃𐖄𐖅𐖆𐖇𐖈𐖉𐖊𐖌𐖍𐖎𐖏𐖐𐖑𐖒𐖔𐖕𐲀𐲁𐲂𐲃𐲄𐲅𐲆𐲇𐲈𐲉𐲊𐲋𐲌𐲍𐲎𐲏𐲐𐲑𐲒𐲓𐲔𐲕𐲖𐲗𐲘𐲙𐲚𐲛𐲜𐲝𐲞𐲟𐲠𐲡𐲢𐲣𐲤𐲥𐲦𐲧𐲨𐲩𐲪𐲫𐲬𐲭𐲮𐲯𐲰𐲱𐲲𑢠𑢡𑢢𑢣𑢤𑢥𑢦𑢧𑢨𑢩𑢪𑢫𑢬𑢭𑢮𑢯𑢰𑢱𑢲𑢳𑢴𑢵𑢶𑢷𑢸𑢹𑢺𑢻𑢼𑢽𑢾𑢿𖹀𖹁𖹂𖹃𖹄𖹅𖹆𖹇𖹈𖹉𖹊𖹋𖹌𖹍𖹎𖹏𖹐𖹑𖹒𖹓𖹔𖹕𖹖𖹗𖹘𖹙𖹚𖹛𖹜𖹝𖹞𖹟𞤀𞤁𞤂𞤃𞤄𞤅𞤆𞤇𞤈𞤉𞤊𞤋𞤌𞤍𞤎𞤏𞤐𞤑𞤒𞤓𞤔𞤕𞤖𞤗𞤘𞤙𞤚𞤛𞤜𞤝𞤞𞤟𞤠𞤡');
$project_identity_v1$;
CREATE OR REPLACE FUNCTION public.project_identity_equals(left_value text, right_value text) RETURNS boolean
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $project_identity_v1$
    SELECT public.project_identity_key(left_value) = public.project_identity_key(right_value);
$project_identity_v1$;

CREATE OR REPLACE FUNCTION public.project_cache_scope(value text) RETURNS text
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $project_identity_v1$
    SELECT CASE WHEN left(value, 8) = 'project:'
        THEN 'project:' || public.project_identity_key(substr(value, 9)) ELSE value END
$project_identity_v1$;

-- Clone only audited identity indexes; retain every other key, predicate and NULL
-- uniqueness rule. Historical telemetry grouping and revision alias rows stay intact.
-- Preflight every scope before creating indexes. The migration runner's transaction
-- rolls back functions, indexes and its receipt together if any collision is found.
DO $project_identity_indexes$
DECLARE
    index_name text;
    index_info record;
    key_info record;
    identity_keys text;
    nonnull_keys text;
    predicate_sql text;
    collision_found boolean;
    index_statements text[] := ARRAY[]::text[];
    index_statement text;
BEGIN
    FOREACH index_name IN ARRAY ARRAY[
        'ix_memory_items_project_owner_external_key',
        'ix_tenant_project_grants_tenant_project',
        'ix_source_connections_owner_project_name',
        'ix_project_hierarchies_owner_dimension_parent_child',
        'ix_project_security_revisions_owner_project',
        'ix_project_authorization_policies_lookup',
        'ix_project_explicit_grants_lookup',
        'ix_canonical_tag_definitions_owner_project_name',
        'ix_canonical_tag_aliases_scope_alias',
        'ix_canonical_tag_bindings_scope_unique',
        'ix_file_relations_identity',
        'ix_secret_relations_identity',
        'ix_secrets_scope_name'
    ] LOOP
        SELECT i.*, c.relnamespace::regnamespace AS index_schema,
            i.indrelid::regclass AS table_name
        INTO STRICT index_info
        FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
        WHERE c.oid = to_regclass('public.' || index_name);
        IF NOT index_info.indisunique OR index_info.indexprs IS NOT NULL
            OR index_info.indnkeyatts <> index_info.indnatts THEN
            RAISE EXCEPTION 'PROJECT_IDENTITY_INDEX_CONTRACT: %', index_name;
        END IF;
        identity_keys := '';
        nonnull_keys := '';
        FOR key_info IN
            SELECT a.attname, k.ordinality
            FROM unnest(index_info.indkey) WITH ORDINALITY k(attnum, ordinality)
            JOIN pg_attribute a ON a.attrelid = index_info.indrelid AND a.attnum = k.attnum
            ORDER BY k.ordinality
        LOOP
            identity_keys := identity_keys || CASE WHEN identity_keys = '' THEN '' ELSE ', ' END ||
                CASE WHEN key_info.attname IN ('project_id', 'parent_project_id', 'child_project_id', 'target_project_id')
                    THEN format('public.project_identity_key(%I)', key_info.attname)
                    ELSE format('%I', key_info.attname) END;
            nonnull_keys := nonnull_keys || CASE WHEN nonnull_keys = '' THEN '' ELSE ' AND ' END ||
                format('%I IS NOT NULL', key_info.attname);
        END LOOP;
        predicate_sql := COALESCE(pg_get_expr(index_info.indpred, index_info.indrelid), 'TRUE');
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE (%s) AND (%s) GROUP BY %s HAVING count(*) > 1)',
            index_info.table_name, predicate_sql,
            CASE WHEN index_info.indnullsnotdistinct THEN 'TRUE' ELSE nonnull_keys END,
            identity_keys) INTO collision_found;
        IF collision_found THEN
            RAISE EXCEPTION 'PROJECT_IDENTITY_COLLISION: %', index_name;
        END IF;
        index_statements := array_append(index_statements,
            format('CREATE UNIQUE INDEX %I ON %s (%s)%s WHERE %s',
                'pi1_' || index_name, index_info.table_name, identity_keys,
                CASE WHEN index_info.indnullsnotdistinct THEN ' NULLS NOT DISTINCT' ELSE '' END,
                predicate_sql));
    END LOOP;

    IF EXISTS (SELECT 1 FROM monitoring.projection_states
        GROUP BY projection_name, tenant_scope_key, public.project_identity_key(project_id)
        HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM authority.background_runs WHERE status = 'Running'
            GROUP BY tenant_id, public.project_identity_key(project_id), job_type, mode
            HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM skill_bindings WHERE scope = 'Project'
            GROUP BY skill_id, scope, public.project_identity_key(scope_value) HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM memory_items WHERE external_key = 'system:project-information'
            GROUP BY tenant_id, owner_user_id, public.project_identity_key(project_id) HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM discussion_participants
            GROUP BY thread_id, public.project_identity_key(project_id) HAVING count(*) > 1)
        OR EXISTS (SELECT 1 FROM memory_items
            WHERE public.project_identity_key(project_id) = 'SHARED'
                AND tenant_id IS NULL AND owner_user_id IS NULL
                AND memory_type = 'Summary' AND source_type = 'summary-layer'
                AND external_key LIKE 'shared-summary:%'
            GROUP BY public.project_identity_key(substr(external_key, 16)) HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'PROJECT_IDENTITY_COLLISION: authority scope';
    END IF;
    IF EXISTS (SELECT 1 FROM memory_items
        WHERE public.project_identity_key(project_id) = 'SHARED'
            AND tenant_id IS NULL AND owner_user_id IS NULL
            AND memory_type = 'Summary' AND source_type = 'summary-layer'
            AND external_key LIKE 'shared-summary:%'
            AND public.project_identity_equals(source_ref, substr(external_key, 16)) IS NOT TRUE) THEN
        RAISE EXCEPTION 'PROJECT_IDENTITY_REFERENCE_CONTRACT: shared summary';
    END IF;
    FOREACH index_statement IN ARRAY index_statements LOOP
        EXECUTE index_statement;
    END LOOP;
END
$project_identity_indexes$;

CREATE UNIQUE INDEX pi1_projection_states_identity ON monitoring.projection_states
    (projection_name, tenant_scope_key, public.project_identity_key(project_id));
CREATE UNIQUE INDEX pi1_background_runs_active_identity ON authority.background_runs
    (tenant_id, public.project_identity_key(project_id), job_type, mode) NULLS NOT DISTINCT WHERE status = 'Running';
CREATE UNIQUE INDEX pi1_skill_bindings_project_identity ON skill_bindings
    (skill_id, scope, public.project_identity_key(scope_value)) WHERE scope = 'Project';
CREATE UNIQUE INDEX pi1_project_information_identity ON memory_items
    (tenant_id, owner_user_id, public.project_identity_key(project_id)) NULLS NOT DISTINCT
    WHERE external_key = 'system:project-information';
CREATE UNIQUE INDEX pi1_discussion_participant_identity ON discussion_participants
    (thread_id, public.project_identity_key(project_id));
-- Only the generated source-project suffix is an identity, not arbitrary external keys.
CREATE UNIQUE INDEX pi1_shared_summary_source_project_identity ON memory_items
    (public.project_identity_key(substr(external_key, 16)))
    WHERE public.project_identity_key(project_id) = 'SHARED'
        AND tenant_id IS NULL AND owner_user_id IS NULL
        AND memory_type = 'Summary' AND source_type = 'summary-layer'
        AND external_key LIKE 'shared-summary:%';

CREATE INDEX pi1_memory_items_scope ON memory_items
    (tenant_id, owner_user_id, public.project_identity_key(project_id), status, updated_at DESC);
CREATE INDEX pi1_outbox_scope_sequence ON audit.authority_outbox_events
    (tenant_id, public.project_identity_key(project_id), sequence);
CREATE INDEX pi1_activity_scope_sequence ON monitoring.activity_projections
    (tenant_id, public.project_identity_key(project_id), authority_sequence);
CREATE INDEX pi1_cache_revision_alias_scope ON cache_scope_revisions (public.project_cache_scope(scope));
CREATE INDEX pi1_runtime_logs_project_created ON runtime_log_entries
    (public.project_identity_key(project_id), created_at DESC);
