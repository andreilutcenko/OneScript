﻿Перем юТест;

Функция ПолучитьСписокТестов(ЮнитТестирование) Экспорт
	
	юТест = ЮнитТестирование;
	
	ВсеТесты = Новый Массив;
	
	ВсеТесты.Добавить("ТестДолжен_ПроверитьВычислениеПростогоВыражения");
	ВсеТесты.Добавить("ТестДолжен_ПроверитьВычислениеВызоваФункции");
	ВсеТесты.Добавить("ТестДолжен_ПроверитьВычислениеБроскаИсключения");
	
	Возврат ВсеТесты;
КонецФункции

Процедура ТестДолжен_ПроверитьВычислениеПростогоВыражения() Экспорт

	юТест.ПроверитьРавенство(4, Вычислить("2 + 2"));
	
	ВнешняяПеременная = 1;
	юТест.ПроверитьРавенство(1, Вычислить("ВнешняяПеременная"));

КонецПроцедуры

Функция НехорошийМетод()
	ВызватьИсключение "ААА";
КонецФункции

Функция ХорошийМетод()
	Возврат Сред("ААА",2,1);
КонецФункции

Процедура ТестДолжен_ПроверитьВычислениеВызоваФункции() Экспорт
	Текст = "";
	Для Сч = 1 По 3 Цикл
		Текст = Текст + Вычислить("ХорошийМетод()");
	КонецЦикла;
	
	юТест.ПроверитьРавенство("ААА", Текст);
	
КонецПроцедуры

Процедура ТестДолжен_ПроверитьВычислениеБроскаИсключения() Экспорт
	
	Перем ОК;
	
	Попытка
		А = Вычислить("НехорошийМетод()");
	Исключение
		ТекстОшибки = ИнформацияОбОшибке().Описание;
		Сообщить("Получено исключение: " + ТекстОшибки);
		ОК = Истина;
	КонецПопытки;
	
	юТест.ПроверитьИстину(ОК, "Проверяем, что после исключения вернулись в тот же кадр стека вызовов");
		
КонецПроцедуры